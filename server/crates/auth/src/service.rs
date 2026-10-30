use std::sync::Arc;

use serde::Serialize;
use serde_json::Value;
use sqlx::{PgPool, Postgres, Transaction};
use time::{format_description::well_known::Rfc3339, Duration, OffsetDateTime};
use uuid::Uuid;

use crate::{
    clock::SharedClock,
    crypto::{bot_link, hash, random_secret, validate_device_name, validate_username},
    error::{AuthError, AuthResult},
    telegram::{
        BotClient, InlineButton, SentMessage, TelegramCallbackQuery, TelegramMessage,
        TelegramUpdate, TelegramUser,
    },
};

const REQUEST_LIFETIME: Duration = Duration::minutes(5);
const SESSION_LIFETIME: Duration = Duration::days(14);
const TELEGRAM_SESSION_LIST_LIMIT: i64 = 20;

#[derive(Clone)]
pub struct AuthService {
    pool: PgPool,
    clock: SharedClock,
}

impl AuthService {
    pub fn new(pool: PgPool, clock: SharedClock) -> Self {
        Self { pool, clock }
    }

    pub fn system(pool: PgPool) -> Self {
        Self::new(pool, Arc::new(crate::clock::SystemClock))
    }

    pub(crate) fn clock_now(&self) -> OffsetDateTime {
        self.clock.now()
    }

    pub async fn register(
        &self,
        device_name: &str,
        bot_username: &str,
    ) -> AuthResult<RegisterResponse> {
        if !validate_device_name(device_name) || bot_username.trim().is_empty() {
            return Err(AuthError::bad_request());
        }
        let now = self.clock.now();
        let expires_at = now + REQUEST_LIFETIME;
        let request_id = Uuid::new_v4();
        let (code, code_hash) = random_secret(24)?;
        let (exchange_secret, exchange_secret_hash) = random_secret(32)?;

        sqlx::query(
            "INSERT INTO auth_requests \
             (id, kind, status, device_name, code_hash, exchange_secret_hash, created_at, expires_at) \
             VALUES ($1, 'register', 'pending', $2, $3, $4, $5, $6)",
        )
        .bind(request_id)
        .bind(device_name)
        .bind(code_hash)
        .bind(exchange_secret_hash)
        .bind(now)
        .bind(expires_at)
        .execute(&self.pool)
        .await?;

        Ok(RegisterResponse {
            request_id,
            bot_link: bot_link(bot_username, &code),
            exchange_secret,
            expires_at: format_timestamp(expires_at)?,
        })
    }

    pub async fn login(&self, username: &str, device_name: &str) -> AuthResult<LoginResponse> {
        if !validate_username(username) || !validate_device_name(device_name) {
            return Err(AuthError::bad_request());
        }
        let now = self.clock.now();
        let expires_at = now + REQUEST_LIFETIME;
        let request_id = Uuid::new_v4();
        let (exchange_secret, exchange_secret_hash) = random_secret(32)?;
        let mut transaction = self.pool.begin().await?;

        let account = sqlx::query_as::<_, (Uuid, String, i64)>(
            "SELECT id, username, chat_id FROM accounts \
             WHERE lower(username) = lower($1) ORDER BY updated_at DESC, created_at DESC LIMIT 1",
        )
        .bind(username)
        .fetch_optional(&mut *transaction)
        .await?;
        let Some((account_id, stored_username, chat_id)) = account else {
            return Err(AuthError::new(
                axum::http::StatusCode::NOT_FOUND,
                "account_not_found",
                "Account not found.",
            ));
        };

        sqlx::query(
            "INSERT INTO auth_requests \
             (id, kind, status, device_name, exchange_secret_hash, username, account_id, chat_id, created_at, expires_at) \
             VALUES ($1, 'login', 'pending', $2, $3, $4, $5, $6, $7, $8)",
        )
        .bind(request_id)
        .bind(device_name)
        .bind(exchange_secret_hash)
        .bind(&stored_username)
        .bind(account_id)
        .bind(chat_id)
        .bind(now)
        .bind(expires_at)
        .execute(&mut *transaction)
        .await?;

        enqueue_effect(
            &mut transaction,
            &BotEffect::SendMessage {
                chat_id,
                text: format!(
                    "Запрос на вход с устройства «{device_name}». Подтвердите вход, если это вы."
                ),
                keyboard: vec![vec![
                    InlineButton {
                        text: "Подтвердить".to_owned(),
                        callback_data: format!("login:approve:{request_id}"),
                    },
                    InlineButton {
                        text: "Отклонить".to_owned(),
                        callback_data: format!("login:deny:{request_id}"),
                    },
                ]],
                auth_request_id: Some(request_id.to_string()),
            },
            now,
        )
        .await?;
        transaction.commit().await?;

        Ok(LoginResponse {
            request_id,
            exchange_secret,
            expires_at: format_timestamp(expires_at)?,
        })
    }

    pub async fn request_status(
        &self,
        request_id: Uuid,
        exchange_secret: &str,
    ) -> AuthResult<RequestStatusResponse> {
        let now = self.clock.now();
        let mut transaction = self.pool.begin().await?;
        let row = sqlx::query_as::<_, (String, OffsetDateTime)>(
            "SELECT status, expires_at FROM auth_requests \
             WHERE id = $1 AND exchange_secret_hash = $2 FOR UPDATE",
        )
        .bind(request_id)
        .bind(hash(exchange_secret.as_bytes()))
        .fetch_optional(&mut *transaction)
        .await?;
        let Some((mut status, expires_at)) = row else {
            return Err(AuthError::unauthorized());
        };

        if expires_at <= now && (status == "pending" || status == "approved") {
            sqlx::query(
                "UPDATE auth_requests SET status = 'expired', completed_at = $2 \
                 WHERE id = $1 AND status IN ('pending', 'approved')",
            )
            .bind(request_id)
            .bind(now)
            .execute(&mut *transaction)
            .await?;
            status = "expired".to_owned();
        }
        transaction.commit().await?;

        Ok(RequestStatusResponse {
            status,
            expires_at: format_timestamp(expires_at)?,
        })
    }

    pub async fn exchange(
        &self,
        request_id: Uuid,
        exchange_secret: &str,
    ) -> AuthResult<ExchangeResponse> {
        let now = self.clock.now();
        let (token, token_hash) = random_secret(32)?;
        let token_expires_at = now + SESSION_LIFETIME;
        for _ in 0..3 {
            let mut transaction = self.pool.begin().await?;
            let account_id_hint = sqlx::query_scalar::<_, Option<Uuid>>(
                "SELECT account_id FROM auth_requests WHERE id = $1 AND exchange_secret_hash = $2",
            )
            .bind(request_id)
            .bind(hash(exchange_secret.as_bytes()))
            .fetch_optional(&mut *transaction)
            .await?
            .flatten();
            if let Some(account_id) = account_id_hint {
                lock_account(&mut transaction, account_id).await?;
            }
            let request = sqlx::query_as::<_, (String, OffsetDateTime, Option<Uuid>)>(
                "SELECT status, expires_at, account_id FROM auth_requests \
                 WHERE id = $1 AND exchange_secret_hash = $2 FOR UPDATE",
            )
            .bind(request_id)
            .bind(hash(exchange_secret.as_bytes()))
            .fetch_optional(&mut *transaction)
            .await?;
            let Some((status, expires_at, account_id)) = request else {
                return Err(AuthError::unauthorized());
            };
            if account_id != account_id_hint {
                transaction.rollback().await?;
                continue;
            }

            if expires_at <= now && (status == "pending" || status == "approved") {
                sqlx::query(
                    "UPDATE auth_requests SET status = 'expired', completed_at = $2 \
                     WHERE id = $1 AND status IN ('pending', 'approved')",
                )
                .bind(request_id)
                .bind(now)
                .execute(&mut *transaction)
                .await?;
                transaction.commit().await?;
                return Err(AuthError::new(
                    axum::http::StatusCode::GONE,
                    "request_expired",
                    "Authorization request expired.",
                ));
            }

            match status.as_str() {
                "pending" => {
                    return Err(AuthError::new(
                        axum::http::StatusCode::CONFLICT,
                        "request_pending",
                        "Authorization request is still pending.",
                    ));
                }
                "denied" => {
                    return Err(AuthError::new(
                        axum::http::StatusCode::FORBIDDEN,
                        "request_denied",
                        "Authorization request was denied.",
                    ));
                }
                "expired" => {
                    return Err(AuthError::new(
                        axum::http::StatusCode::GONE,
                        "request_expired",
                        "Authorization request expired.",
                    ));
                }
                "account_exists" => {
                    return Err(AuthError::new(
                        axum::http::StatusCode::CONFLICT,
                        "account_exists",
                        "A Telegram account already exists. Use login.",
                    ));
                }
                "consumed" => {
                    return Err(AuthError::new(
                        axum::http::StatusCode::CONFLICT,
                        "request_consumed",
                        "Authorization request was already exchanged.",
                    ));
                }
                "approved" => {}
                _ => return Err(AuthError::internal()),
            }

            let account_id = account_id.ok_or_else(AuthError::internal)?;
            let account = sqlx::query_as::<_, (String, OffsetDateTime)>(
                "SELECT username, created_at FROM accounts WHERE id = $1",
            )
            .bind(account_id)
            .fetch_optional(&mut *transaction)
            .await?
            .ok_or_else(AuthError::internal)?;
            let session_id = Uuid::new_v4();
            let device_name = sqlx::query_scalar::<_, String>(
                "SELECT device_name FROM auth_requests WHERE id = $1",
            )
            .bind(request_id)
            .fetch_one(&mut *transaction)
            .await?;

            sqlx::query(
                "INSERT INTO sessions \
                 (id, account_id, token_hash, device_name, created_at, last_used_at, expires_at) \
                 VALUES ($1, $2, $3, $4, $5, $5, $6)",
            )
            .bind(session_id)
            .bind(account_id)
            .bind(token_hash)
            .bind(device_name)
            .bind(now)
            .bind(token_expires_at)
            .execute(&mut *transaction)
            .await?;
            let consumed = sqlx::query(
                "UPDATE auth_requests SET status = 'consumed', completed_at = $2 \
                 WHERE id = $1 AND status = 'approved' AND expires_at > $2",
            )
            .bind(request_id)
            .bind(now)
            .execute(&mut *transaction)
            .await?;
            if consumed.rows_affected() != 1 {
                return Err(AuthError::new(
                    axum::http::StatusCode::CONFLICT,
                    "request_consumed",
                    "Authorization request was already exchanged.",
                ));
            }
            transaction.commit().await?;

            return Ok(ExchangeResponse {
                token,
                token_expires_at: format_timestamp(token_expires_at)?,
                account: AccountResponse {
                    id: account_id,
                    username: account.0,
                    created_at: format_timestamp(account.1)?,
                },
            });
        }

        Err(AuthError::new(
            axum::http::StatusCode::CONFLICT,
            "request_changed",
            "Authorization request changed while being exchanged.",
        ))
    }

    pub async fn me(&self, token: &str) -> AuthResult<MeResponse> {
        let now = self.clock.now();
        let mut transaction = self.pool.begin().await?;
        let session = renew_session(&mut transaction, token, now).await?;
        let account = sqlx::query_as::<_, (String, OffsetDateTime)>(
            "SELECT username, created_at FROM accounts WHERE id = $1",
        )
        .bind(session.account_id)
        .fetch_optional(&mut *transaction)
        .await?
        .ok_or_else(AuthError::internal)?;
        transaction.commit().await?;

        Ok(MeResponse {
            account: AccountResponse {
                id: session.account_id,
                username: account.0,
                created_at: format_timestamp(account.1)?,
            },
            session: MeSessionResponse {
                device_name: session.device_name,
                expires_at: format_timestamp(session.expires_at)?,
            },
        })
    }

    pub async fn record_playtime(&self, token: &str, id: Uuid, duration_ms: i64) -> AuthResult<()> {
        if id.is_nil() || duration_ms < 0 {
            return Err(AuthError::bad_request());
        }
        let mut tx = self.pool.begin().await?;
        let session = renew_session(&mut tx, token, self.clock.now()).await?;
        let inserted = sqlx::query("INSERT INTO playtime_sessions (id, account_id, duration_ms) VALUES ($1,$2,$3) ON CONFLICT (id) DO NOTHING")
            .bind(id).bind(session.account_id).bind(duration_ms).execute(&mut *tx).await?.rows_affected();
        if inserted == 0 {
            let existing = sqlx::query_as::<_, (Uuid, i64)>(
                "SELECT account_id, duration_ms FROM playtime_sessions WHERE id=$1",
            )
            .bind(id)
            .fetch_one(&mut *tx)
            .await?;
            if existing != (session.account_id, duration_ms) {
                return Err(AuthError::new(
                    axum::http::StatusCode::CONFLICT,
                    "playtime_session_conflict",
                    "Playtime session conflicts with an existing record.",
                ));
            }
        } else {
            sqlx::query("INSERT INTO playtime_totals (account_id, duration_ms) VALUES ($1,$2) ON CONFLICT (account_id) DO UPDATE SET duration_ms=playtime_totals.duration_ms + EXCLUDED.duration_ms")
                .bind(session.account_id).bind(duration_ms).execute(&mut *tx).await?;
        }
        tx.commit().await?;
        Ok(())
    }

    pub async fn playtime(&self, token: &str) -> AuthResult<i64> {
        let mut tx = self.pool.begin().await?;
        let session = renew_session(&mut tx, token, self.clock.now()).await?;
        let total = sqlx::query_scalar::<_, i64>(
            "SELECT duration_ms FROM playtime_totals WHERE account_id=$1",
        )
        .bind(session.account_id)
        .fetch_optional(&mut *tx)
        .await?
        .unwrap_or(0);
        tx.commit().await?;
        Ok(total)
    }

    pub async fn logout(&self, token: &str) -> AuthResult<()> {
        let now = self.clock.now();
        let mut transaction = self.pool.begin().await?;
        let session = renew_session(&mut transaction, token, now).await?;
        let result =
            sqlx::query("UPDATE sessions SET revoked_at = $2 WHERE id = $1 AND revoked_at IS NULL")
                .bind(session.id)
                .bind(now)
                .execute(&mut *transaction)
                .await?;
        if result.rows_affected() != 1 {
            return Err(AuthError::unauthorized());
        }
        transaction.commit().await?;
        Ok(())
    }

    pub async fn check_token(&self, token: &str) -> AuthResult<TokenCheckResponse> {
        let now = self.clock.now();
        let mut transaction = self.pool.begin().await?;
        let session = renew_session(&mut transaction, token, now).await?;
        let username =
            sqlx::query_scalar::<_, String>("SELECT username FROM accounts WHERE id = $1")
                .bind(session.account_id)
                .fetch_optional(&mut *transaction)
                .await?
                .ok_or_else(AuthError::internal)?;
        transaction.commit().await?;

        Ok(TokenCheckResponse {
            account_id: session.account_id,
            username,
            session_id: session.id,
            expires_at: format_timestamp(session.expires_at)?,
        })
    }

    pub async fn active_sessions(&self, telegram_id: i64) -> AuthResult<Vec<ActiveSession>> {
        let now = self.clock.now();
        let sessions = sqlx::query_as::<_, (Uuid, String, OffsetDateTime)>(
            "SELECT s.id, s.device_name, s.expires_at FROM sessions s \
             JOIN accounts a ON a.id = s.account_id \
             WHERE a.telegram_id = $1 AND s.revoked_at IS NULL AND s.expires_at > $2 \
             ORDER BY s.created_at DESC",
        )
        .bind(telegram_id)
        .bind(now)
        .fetch_all(&self.pool)
        .await?;

        sessions
            .into_iter()
            .map(|(id, device_name, expires_at)| {
                Ok(ActiveSession {
                    id,
                    device_name,
                    expires_at: format_timestamp(expires_at)?,
                })
            })
            .collect()
    }

    pub async fn revoke_session(&self, telegram_id: i64, session_id: Uuid) -> AuthResult<bool> {
        let now = self.clock.now();
        let mut transaction = self.pool.begin().await?;
        let account_id = sqlx::query_scalar::<_, Uuid>(
            "SELECT s.account_id FROM sessions s JOIN accounts a ON a.id = s.account_id \
             WHERE s.id = $1 AND a.telegram_id = $2 AND s.revoked_at IS NULL",
        )
        .bind(session_id)
        .bind(telegram_id)
        .fetch_optional(&mut *transaction)
        .await?;
        let Some(account_id) = account_id else {
            return Ok(false);
        };
        lock_account(&mut transaction, account_id).await?;
        let result = sqlx::query(
            "UPDATE sessions SET revoked_at = $3 WHERE id = $1 AND account_id = $2 \
             AND revoked_at IS NULL",
        )
        .bind(session_id)
        .bind(account_id)
        .bind(now)
        .execute(&mut *transaction)
        .await?;
        transaction.commit().await?;
        Ok(result.rows_affected() == 1)
    }

    pub async fn revoke_all(&self, telegram_id: i64) -> AuthResult<bool> {
        let now = self.clock.now();
        let mut transaction = self.pool.begin().await?;
        let account_id =
            sqlx::query_scalar::<_, Uuid>("SELECT id FROM accounts WHERE telegram_id = $1")
                .bind(telegram_id)
                .fetch_optional(&mut *transaction)
                .await?;
        let Some(account_id) = account_id else {
            return Ok(false);
        };
        lock_account(&mut transaction, account_id).await?;
        sqlx::query(
            "UPDATE sessions SET revoked_at = $2 WHERE account_id = $1 AND revoked_at IS NULL",
        )
        .bind(account_id)
        .bind(now)
        .execute(&mut *transaction)
        .await?;
        sqlx::query(
            "UPDATE auth_requests SET status = 'expired', completed_at = $2 \
             WHERE account_id = $1 AND status IN ('pending', 'approved')",
        )
        .bind(account_id)
        .bind(now)
        .execute(&mut *transaction)
        .await?;
        transaction.commit().await?;
        Ok(true)
    }

    pub async fn telegram_offset(&self) -> AuthResult<i64> {
        sqlx::query_scalar::<_, i64>(
            "SELECT update_offset FROM telegram_state WHERE singleton = TRUE",
        )
        .fetch_one(&self.pool)
        .await
        .map_err(Into::into)
    }

    pub async fn process_update(&self, update: &TelegramUpdate) -> AuthResult<()> {
        let now = self.clock.now();
        let mut transaction = self.pool.begin().await?;
        let inserted = sqlx::query(
            "INSERT INTO telegram_updates (update_id, processed_at) VALUES ($1, $2) \
             ON CONFLICT (update_id) DO NOTHING",
        )
        .bind(update.update_id)
        .bind(now)
        .execute(&mut *transaction)
        .await?;
        if inserted.rows_affected() == 0 {
            transaction.commit().await?;
            return Ok(());
        }
        sqlx::query(
            "UPDATE telegram_state SET update_offset = GREATEST(update_offset, $1) \
             WHERE singleton = TRUE",
        )
        .bind(update.update_id.saturating_add(1))
        .execute(&mut *transaction)
        .await?;

        if let Some(sender) = update_sender(update) {
            if let Some(username) = sender.username.as_deref() {
                sqlx::query(
                    "UPDATE accounts SET username = $2, updated_at = $3 \
                     WHERE telegram_id = $1 AND username IS DISTINCT FROM $2",
                )
                .bind(sender.id)
                .bind(username)
                .bind(now)
                .execute(&mut *transaction)
                .await?;
            }
        }

        if let Some(message) = &update.message {
            self.process_message(&mut transaction, message, now).await?;
        }
        if let Some(callback) = &update.callback_query {
            self.process_callback(&mut transaction, callback, now)
                .await?;
        }
        transaction.commit().await?;
        Ok(())
    }

    pub async fn drain_outbox(&self, bot: &dyn BotClient) -> AuthResult<bool> {
        let row = sqlx::query_as::<_, (i64, Value)>(
            "SELECT id, payload FROM telegram_outbox \
             WHERE delivered_at IS NULL AND failed_at IS NULL ORDER BY id LIMIT 1",
        )
        .fetch_optional(&self.pool)
        .await?;
        let Some((outbox_id, payload)) = row else {
            return Ok(false);
        };
        let effect: BotEffect =
            serde_json::from_value(payload).map_err(|_| AuthError::internal())?;
        let now = self.clock.now();
        let delivery_result = match effect {
            BotEffect::SendMessage {
                chat_id,
                text,
                keyboard,
                auth_request_id,
            } => {
                if let Some(request_id) = auth_request_id {
                    let request_id =
                        Uuid::parse_str(&request_id).map_err(|_| AuthError::internal())?;
                    let pending = sqlx::query_scalar::<_, bool>(
                        "SELECT status = 'pending' AND expires_at > $2 \
                         FROM auth_requests WHERE id = $1",
                    )
                    .bind(request_id)
                    .bind(now)
                    .fetch_optional(&self.pool)
                    .await?;
                    let pending = matches!(pending, Some(true));
                    if pending {
                        match bot.send_message(chat_id, &text, &keyboard).await {
                            Ok(sent) => {
                                save_message_reference(&self.pool, request_id, sent).await?;
                                Ok(())
                            }
                            Err(error) => Err(error),
                        }
                    } else {
                        Ok(())
                    }
                } else {
                    bot.send_message(chat_id, &text, &keyboard)
                        .await
                        .map(|_| ())
                }
            }
            BotEffect::AnswerCallback {
                callback_query_id,
                text,
            } => {
                bot.answer_callback_query(&callback_query_id, text.as_deref())
                    .await
            }
            BotEffect::EditMessage {
                chat_id,
                message_id,
                text,
            } => bot.edit_message_text(chat_id, message_id, &text).await,
        };
        if let Err(error) = delivery_result {
            if error.is_permanent() {
                sqlx::query(
                    "UPDATE telegram_outbox SET failed_at = $2, failure_reason = $3 \
                     WHERE id = $1 AND delivered_at IS NULL AND failed_at IS NULL",
                )
                .bind(outbox_id)
                .bind(now)
                .bind("Telegram rejected the request with a client error")
                .execute(&self.pool)
                .await?;
                tracing::error!(
                    outbox_id,
                    "permanent Telegram outbox delivery failure; skipped"
                );
                return Ok(true);
            }
            return Err(AuthError::internal());
        }
        sqlx::query(
            "UPDATE telegram_outbox SET delivered_at = $2 \
             WHERE id = $1 AND delivered_at IS NULL AND failed_at IS NULL",
        )
        .bind(outbox_id)
        .bind(now)
        .execute(&self.pool)
        .await?;
        Ok(true)
    }

    async fn process_message(
        &self,
        transaction: &mut Transaction<'_, Postgres>,
        message: &TelegramMessage,
        now: OffsetDateTime,
    ) -> AuthResult<()> {
        let Some(sender) = &message.from else {
            return Ok(());
        };
        let Some(text) = message.text.as_deref() else {
            return Ok(());
        };
        let mut parts = text.split_whitespace();
        let Some(command) = parts.next() else {
            return Ok(());
        };
        if is_command(command, "/start") {
            let code = parts.next();
            self.handle_start(transaction, message, sender, code, now)
                .await
        } else if is_command(command, "/sessions") {
            self.handle_sessions(transaction, message, sender, now)
                .await
        } else {
            Ok(())
        }
    }

    async fn process_callback(
        &self,
        transaction: &mut Transaction<'_, Postgres>,
        callback: &TelegramCallbackQuery,
        now: OffsetDateTime,
    ) -> AuthResult<()> {
        let Some(data) = callback.data.as_deref() else {
            enqueue_effect(
                transaction,
                &BotEffect::AnswerCallback {
                    callback_query_id: callback.id.clone(),
                    text: None,
                },
                now,
            )
            .await?;
            return Ok(());
        };
        let parts = data.split(':').collect::<Vec<_>>();
        match parts.as_slice() {
            ["login", action @ ("approve" | "deny"), request_id] => {
                self.handle_login_callback(transaction, callback, action, request_id, now)
                    .await
            }
            ["session", "revoke", session_id] => {
                self.handle_revoke_session_callback(transaction, callback, session_id, now)
                    .await
            }
            ["sessions", "revoke_all"] => {
                self.handle_revoke_all_callback(transaction, callback, now)
                    .await
            }
            _ => {
                enqueue_effect(
                    transaction,
                    &BotEffect::AnswerCallback {
                        callback_query_id: callback.id.clone(),
                        text: Some("Действие недоступно.".to_owned()),
                    },
                    now,
                )
                .await
            }
        }
    }

    async fn handle_start(
        &self,
        transaction: &mut Transaction<'_, Postgres>,
        message: &TelegramMessage,
        sender: &TelegramUser,
        code: Option<&str>,
        now: OffsetDateTime,
    ) -> AuthResult<()> {
        if message.chat.kind != "private" {
            return enqueue_text(
                transaction,
                message.chat.id,
                "Откройте личный чат с ботом.",
                now,
            )
            .await;
        }
        let Some(code) = code else {
            return enqueue_text(
                transaction,
                message.chat.id,
                "Откройте ссылку из приложения Hako для продолжения.",
                now,
            )
            .await;
        };
        let request = sqlx::query_as::<_, (Uuid, String, OffsetDateTime)>(
            "SELECT id, status, expires_at FROM auth_requests \
             WHERE code_hash = $1 AND kind = 'register' FOR UPDATE",
        )
        .bind(hash(code.as_bytes()))
        .fetch_optional(&mut **transaction)
        .await?;
        let Some((request_id, status, expires_at)) = request else {
            return enqueue_text(
                transaction,
                message.chat.id,
                "Ссылка недействительна или истекла.",
                now,
            )
            .await;
        };
        if status != "pending" {
            return enqueue_text(
                transaction,
                message.chat.id,
                "Ссылка недействительна или истекла.",
                now,
            )
            .await;
        }
        if expires_at <= now {
            sqlx::query(
                "UPDATE auth_requests SET status = 'expired', completed_at = $2 WHERE id = $1",
            )
            .bind(request_id)
            .bind(now)
            .execute(&mut **transaction)
            .await?;
            return enqueue_text(
                transaction,
                message.chat.id,
                "Ссылка недействительна или истекла.",
                now,
            )
            .await;
        }
        let Some(username) = sender.username.as_deref() else {
            return enqueue_text(
                transaction,
                message.chat.id,
                "Для создания аккаунта установите имя пользователя Telegram и откройте ссылку снова.",
                now,
            )
            .await;
        };
        let existing_account =
            sqlx::query_scalar::<_, Uuid>("SELECT id FROM accounts WHERE telegram_id = $1")
                .bind(sender.id)
                .fetch_optional(&mut **transaction)
                .await?;
        if existing_account.is_some() {
            sqlx::query(
                "UPDATE auth_requests SET status = 'account_exists', completed_at = $2, \
                 account_id = $3, chat_id = $4 WHERE id = $1",
            )
            .bind(request_id)
            .bind(now)
            .bind(existing_account)
            .bind(message.chat.id)
            .execute(&mut **transaction)
            .await?;
            return enqueue_text(
                transaction,
                message.chat.id,
                "Аккаунт уже существует. Войдите в него из приложения Hako.",
                now,
            )
            .await;
        }

        let account_id = Uuid::new_v4();
        sqlx::query(
            "INSERT INTO accounts (id, telegram_id, username, chat_id, created_at, updated_at) \
             VALUES ($1, $2, $3, $4, $5, $5)",
        )
        .bind(account_id)
        .bind(sender.id)
        .bind(username)
        .bind(message.chat.id)
        .bind(now)
        .execute(&mut **transaction)
        .await?;
        sqlx::query(
            "UPDATE auth_requests SET status = 'approved', completed_at = $2, account_id = $3, \
             chat_id = $4, message_id = $5 WHERE id = $1 AND status = 'pending'",
        )
        .bind(request_id)
        .bind(now)
        .bind(account_id)
        .bind(message.chat.id)
        .bind(message.message_id)
        .execute(&mut **transaction)
        .await?;
        enqueue_text(
            transaction,
            message.chat.id,
            "Аккаунт Hako создан. Вернитесь в приложение для завершения входа.",
            now,
        )
        .await
    }

    async fn handle_sessions(
        &self,
        transaction: &mut Transaction<'_, Postgres>,
        message: &TelegramMessage,
        sender: &TelegramUser,
        now: OffsetDateTime,
    ) -> AuthResult<()> {
        if message.chat.kind != "private" {
            return enqueue_text(
                transaction,
                message.chat.id,
                "Откройте личный чат с ботом.",
                now,
            )
            .await;
        }
        let account_id =
            sqlx::query_scalar::<_, Uuid>("SELECT id FROM accounts WHERE telegram_id = $1")
                .bind(sender.id)
                .fetch_optional(&mut **transaction)
                .await?;
        let Some(account_id) = account_id else {
            return enqueue_text(transaction, message.chat.id, "Аккаунт не найден.", now).await;
        };
        let sessions = sqlx::query_as::<_, (Uuid, String, i64)>(
            "SELECT id, device_name, count(*) OVER() FROM sessions WHERE account_id = $1 \
             AND revoked_at IS NULL AND expires_at > $2 \
             ORDER BY created_at DESC LIMIT $3",
        )
        .bind(account_id)
        .bind(now)
        .bind(TELEGRAM_SESSION_LIST_LIMIT)
        .fetch_all(&mut **transaction)
        .await?;
        let has_pending_requests = sqlx::query_scalar::<_, bool>(
            "SELECT EXISTS (SELECT 1 FROM auth_requests WHERE account_id = $1 \
             AND status IN ('pending', 'approved') AND expires_at > $2)",
        )
        .bind(account_id)
        .bind(now)
        .fetch_one(&mut **transaction)
        .await?;
        if sessions.is_empty() && !has_pending_requests {
            return enqueue_text(transaction, message.chat.id, "Активных сессий нет.", now).await;
        }
        let total_sessions = sessions.first().map_or(0, |(_, _, total)| *total);
        let mut keyboard = Vec::with_capacity(sessions.len() + 1);
        let mut lines = Vec::with_capacity(sessions.len() + 1);
        for (session_id, device_name, _) in &sessions {
            lines.push(format!("• {}", truncate_chars(device_name, 64)));
            keyboard.push(vec![InlineButton {
                text: format!("Завершить: {}", truncate_chars(device_name, 40)),
                callback_data: format!("session:revoke:{session_id}"),
            }]);
        }
        if total_sessions > sessions.len() as i64 {
            lines.push(format!(
                "… и ещё {} сессий.",
                total_sessions - sessions.len() as i64
            ));
        }
        if total_sessions > 0 || has_pending_requests {
            keyboard.push(vec![InlineButton {
                text: "Завершить все сессии".to_owned(),
                callback_data: "sessions:revoke_all".to_owned(),
            }]);
        }
        let text = if sessions.is_empty() {
            "Активных сессий нет. Есть незавершённые запросы на вход.".to_owned()
        } else {
            format!("Активные сессии:\n{}", lines.join("\n"))
        };
        enqueue_effect(
            transaction,
            &BotEffect::SendMessage {
                chat_id: message.chat.id,
                text,
                keyboard,
                auth_request_id: None,
            },
            now,
        )
        .await
    }

    async fn handle_login_callback(
        &self,
        transaction: &mut Transaction<'_, Postgres>,
        callback: &TelegramCallbackQuery,
        action: &str,
        request_id_text: &str,
        now: OffsetDateTime,
    ) -> AuthResult<()> {
        let request_id = Uuid::parse_str(request_id_text).ok();
        let Some(request_id) = request_id else {
            return enqueue_callback_answer(transaction, callback, "Запрос недоступен.", now).await;
        };
        let row = sqlx::query_as::<_, (String, OffsetDateTime, Option<String>, Uuid, i64, i64, Option<i64>)>(
            "SELECT ar.status, ar.expires_at, ar.username, ar.account_id, a.telegram_id, \
             a.chat_id, ar.message_id FROM auth_requests ar \
             JOIN accounts a ON a.id = ar.account_id WHERE ar.id = $1 AND ar.kind = 'login' FOR UPDATE",
        )
        .bind(request_id)
        .fetch_optional(&mut **transaction)
        .await?;
        let Some((
            status,
            expires_at,
            request_username,
            _account_id,
            telegram_id,
            chat_id,
            message_id,
        )) = row
        else {
            return enqueue_callback_answer(transaction, callback, "Запрос недоступен.", now).await;
        };
        let Some(message) = callback.message.as_ref() else {
            return enqueue_callback_answer(transaction, callback, "Запрос недоступен.", now).await;
        };
        let username_matches = callback
            .from
            .username
            .as_deref()
            .zip(request_username.as_deref())
            .is_some_and(|(actual, expected)| actual.eq_ignore_ascii_case(expected));
        let message_matches = message.chat.id == chat_id && message_id == Some(message.message_id);
        if telegram_id != callback.from.id || !username_matches || !message_matches {
            return enqueue_callback_answer(transaction, callback, "Это действие недоступно.", now)
                .await;
        }
        if status != "pending" {
            return enqueue_login_result(transaction, callback, "Запрос уже обработан.", now).await;
        }
        if expires_at <= now {
            sqlx::query(
                "UPDATE auth_requests SET status = 'expired', completed_at = $2 WHERE id = $1 AND status = 'pending'",
            )
            .bind(request_id)
            .bind(now)
            .execute(&mut **transaction)
            .await?;
            return enqueue_login_result(transaction, callback, "Запрос истёк.", now).await;
        }
        let (new_status, text) = if action == "approve" {
            ("approved", "Вход подтверждён.")
        } else {
            ("denied", "Вход отклонён.")
        };
        sqlx::query(
            "UPDATE auth_requests SET status = $2, completed_at = $3 WHERE id = $1 AND status = 'pending'",
        )
        .bind(request_id)
        .bind(new_status)
        .bind(now)
        .execute(&mut **transaction)
        .await?;
        enqueue_login_result(transaction, callback, text, now).await
    }

    async fn handle_revoke_session_callback(
        &self,
        transaction: &mut Transaction<'_, Postgres>,
        callback: &TelegramCallbackQuery,
        session_id_text: &str,
        now: OffsetDateTime,
    ) -> AuthResult<()> {
        let session_id = Uuid::parse_str(session_id_text).ok();
        let message = callback.message.as_ref();
        let Some((session_id, message)) = session_id.zip(message) else {
            return enqueue_callback_answer(transaction, callback, "Сессия недоступна.", now).await;
        };
        let account_id = sqlx::query_scalar::<_, Uuid>(
            "SELECT s.account_id FROM sessions s JOIN accounts a ON a.id = s.account_id \
             WHERE s.id = $1 AND a.telegram_id = $2 AND a.chat_id = $3 \
             AND s.revoked_at IS NULL",
        )
        .bind(session_id)
        .bind(callback.from.id)
        .bind(message.chat.id)
        .fetch_optional(&mut **transaction)
        .await?;
        let Some(account_id) = account_id else {
            return enqueue_callback_answer(transaction, callback, "Сессия недоступна.", now).await;
        };
        lock_account(transaction, account_id).await?;
        let result = sqlx::query(
            "UPDATE sessions SET revoked_at = $3 WHERE id = $1 AND account_id = $2 \
             AND revoked_at IS NULL",
        )
        .bind(session_id)
        .bind(account_id)
        .bind(now)
        .execute(&mut **transaction)
        .await?;
        if result.rows_affected() != 1 {
            return enqueue_callback_answer(transaction, callback, "Сессия недоступна.", now).await;
        }
        enqueue_callback_result_and_edit(
            transaction,
            callback,
            "Сессия завершена.",
            "Сессия устройства завершена.",
            now,
        )
        .await
    }

    async fn handle_revoke_all_callback(
        &self,
        transaction: &mut Transaction<'_, Postgres>,
        callback: &TelegramCallbackQuery,
        now: OffsetDateTime,
    ) -> AuthResult<()> {
        let Some(message) = callback.message.as_ref() else {
            return enqueue_callback_answer(transaction, callback, "Действие недоступно.", now)
                .await;
        };
        let account_id = sqlx::query_scalar::<_, Uuid>(
            "SELECT id FROM accounts WHERE telegram_id = $1 AND chat_id = $2",
        )
        .bind(callback.from.id)
        .bind(message.chat.id)
        .fetch_optional(&mut **transaction)
        .await?;
        let Some(account_id) = account_id else {
            return enqueue_callback_answer(transaction, callback, "Действие недоступно.", now)
                .await;
        };
        lock_account(transaction, account_id).await?;
        sqlx::query(
            "UPDATE sessions SET revoked_at = $2 WHERE account_id = $1 AND revoked_at IS NULL",
        )
        .bind(account_id)
        .bind(now)
        .execute(&mut **transaction)
        .await?;
        sqlx::query(
            "UPDATE auth_requests SET status = 'expired', completed_at = $2 \
             WHERE account_id = $1 AND status IN ('pending', 'approved')",
        )
        .bind(account_id)
        .bind(now)
        .execute(&mut **transaction)
        .await?;
        enqueue_callback_result_and_edit(
            transaction,
            callback,
            "Все сессии завершены.",
            "Все сессии завершены.",
            now,
        )
        .await
    }
}

async fn lock_account(
    transaction: &mut Transaction<'_, Postgres>,
    account_id: Uuid,
) -> AuthResult<()> {
    sqlx::query("SELECT pg_advisory_xact_lock(hashtextextended($1::text, 0))")
        .bind(account_id)
        .execute(&mut **transaction)
        .await?;
    Ok(())
}

fn truncate_chars(value: &str, limit: usize) -> String {
    let mut chars = value.chars();
    let truncated = chars.by_ref().take(limit).collect::<String>();
    if chars.next().is_some() {
        format!("{truncated}…")
    } else {
        truncated
    }
}

#[derive(Serialize, serde::Deserialize)]
pub struct RegisterResponse {
    pub request_id: Uuid,
    pub bot_link: String,
    pub exchange_secret: String,
    pub expires_at: String,
}

#[derive(Serialize, serde::Deserialize)]
pub struct LoginResponse {
    pub request_id: Uuid,
    pub exchange_secret: String,
    pub expires_at: String,
}

#[derive(Serialize, serde::Deserialize)]
pub struct RequestStatusResponse {
    pub status: String,
    pub expires_at: String,
}

#[derive(Serialize, serde::Deserialize)]
pub struct AccountResponse {
    pub id: Uuid,
    pub username: String,
    pub created_at: String,
}

#[derive(Serialize, serde::Deserialize)]
pub struct ExchangeResponse {
    pub token: String,
    pub token_expires_at: String,
    pub account: AccountResponse,
}

#[derive(Serialize, serde::Deserialize)]
pub struct MeSessionResponse {
    pub device_name: String,
    pub expires_at: String,
}

#[derive(Serialize, serde::Deserialize)]
pub struct MeResponse {
    pub account: AccountResponse,
    pub session: MeSessionResponse,
}

#[derive(Serialize)]
pub struct TokenCheckResponse {
    pub account_id: Uuid,
    pub username: String,
    pub session_id: Uuid,
    pub expires_at: String,
}

#[derive(Serialize)]
pub struct ActiveSession {
    pub id: Uuid,
    pub device_name: String,
    pub expires_at: String,
}

#[derive(Serialize, serde::Deserialize)]
#[serde(tag = "kind", rename_all = "snake_case")]
enum BotEffect {
    SendMessage {
        chat_id: i64,
        text: String,
        keyboard: Vec<Vec<InlineButton>>,
        auth_request_id: Option<String>,
    },
    AnswerCallback {
        callback_query_id: String,
        text: Option<String>,
    },
    EditMessage {
        chat_id: i64,
        message_id: i64,
        text: String,
    },
}

struct SessionRow {
    id: Uuid,
    account_id: Uuid,
    device_name: String,
    expires_at: OffsetDateTime,
}

async fn renew_session(
    transaction: &mut Transaction<'_, Postgres>,
    token: &str,
    now: OffsetDateTime,
) -> AuthResult<SessionRow> {
    let expires_at = now + SESSION_LIFETIME;
    sqlx::query_as::<_, (Uuid, Uuid, String, OffsetDateTime)>(
        "UPDATE sessions SET expires_at = $2, last_used_at = $3 \
         WHERE token_hash = $1 AND revoked_at IS NULL AND expires_at > $3 \
         RETURNING id, account_id, device_name, expires_at",
    )
    .bind(hash(token.as_bytes()))
    .bind(expires_at)
    .bind(now)
    .fetch_optional(&mut **transaction)
    .await?
    .map(|(id, account_id, device_name, expires_at)| SessionRow {
        id,
        account_id,
        device_name,
        expires_at,
    })
    .ok_or_else(AuthError::unauthorized)
}

async fn enqueue_effect(
    transaction: &mut Transaction<'_, Postgres>,
    effect: &BotEffect,
    now: OffsetDateTime,
) -> AuthResult<()> {
    let payload = serde_json::to_value(effect).map_err(|_| AuthError::internal())?;
    sqlx::query("INSERT INTO telegram_outbox (payload, created_at) VALUES ($1, $2)")
        .bind(payload)
        .bind(now)
        .execute(&mut **transaction)
        .await?;
    Ok(())
}

async fn enqueue_text(
    transaction: &mut Transaction<'_, Postgres>,
    chat_id: i64,
    text: &str,
    now: OffsetDateTime,
) -> AuthResult<()> {
    enqueue_effect(
        transaction,
        &BotEffect::SendMessage {
            chat_id,
            text: text.to_owned(),
            keyboard: Vec::new(),
            auth_request_id: None,
        },
        now,
    )
    .await
}

async fn enqueue_callback_answer(
    transaction: &mut Transaction<'_, Postgres>,
    callback: &TelegramCallbackQuery,
    text: &str,
    now: OffsetDateTime,
) -> AuthResult<()> {
    enqueue_effect(
        transaction,
        &BotEffect::AnswerCallback {
            callback_query_id: callback.id.clone(),
            text: Some(text.to_owned()),
        },
        now,
    )
    .await
}

async fn enqueue_login_result(
    transaction: &mut Transaction<'_, Postgres>,
    callback: &TelegramCallbackQuery,
    text: &str,
    now: OffsetDateTime,
) -> AuthResult<()> {
    enqueue_callback_result_and_edit(transaction, callback, text, text, now).await
}

async fn enqueue_callback_result_and_edit(
    transaction: &mut Transaction<'_, Postgres>,
    callback: &TelegramCallbackQuery,
    answer: &str,
    edit: &str,
    now: OffsetDateTime,
) -> AuthResult<()> {
    enqueue_callback_answer(transaction, callback, answer, now).await?;
    if let Some(message) = callback.message.as_ref() {
        enqueue_effect(
            transaction,
            &BotEffect::EditMessage {
                chat_id: message.chat.id,
                message_id: message.message_id,
                text: edit.to_owned(),
            },
            now,
        )
        .await?;
    }
    Ok(())
}

async fn save_message_reference(
    pool: &PgPool,
    request_id: Uuid,
    message: SentMessage,
) -> AuthResult<()> {
    sqlx::query("UPDATE auth_requests SET chat_id = $2, message_id = $3 WHERE id = $1")
        .bind(request_id)
        .bind(message.chat_id)
        .bind(message.message_id)
        .execute(pool)
        .await?;
    Ok(())
}

fn update_sender(update: &TelegramUpdate) -> Option<&TelegramUser> {
    update
        .message
        .as_ref()
        .and_then(|message| message.from.as_ref())
        .or_else(|| {
            update
                .callback_query
                .as_ref()
                .map(|callback| &callback.from)
        })
}

fn is_command(value: &str, command: &str) -> bool {
    value == command
        || value
            .strip_prefix(command)
            .is_some_and(|suffix| suffix.starts_with('@'))
}

fn format_timestamp(value: OffsetDateTime) -> AuthResult<String> {
    value.format(&Rfc3339).map_err(|_| AuthError::internal())
}
