use std::{
    env,
    sync::{
        atomic::{AtomicI64, Ordering},
        Arc, Mutex, RwLock,
    },
};

use async_trait::async_trait;
use axum::{
    body::Body,
    http::{Request, StatusCode},
    Router,
};
use hako_auth::{
    api::{internal_router, public_router, AppState},
    bot_worker::run_bot_worker,
    clock::{Clock, SharedClock},
    crypto::hash,
    rate_limit::RateLimiter,
    service::{AuthService, ExchangeResponse, RegisterResponse},
    telegram::{
        BotClient, BotError, InlineButton, SentMessage, TelegramCallbackQuery, TelegramChat,
        TelegramMessage, TelegramUpdate, TelegramUser,
    },
};
use serde::de::DeserializeOwned;
use serde_json::{json, Value};
use sqlx::{postgres::PgPoolOptions, PgPool};
use time::{Duration, OffsetDateTime};
use tokio::sync::Notify;
use tower::ServiceExt;
use uuid::Uuid;

#[derive(Clone)]
struct MockSentMessage {
    chat_id: i64,
    message_id: i64,
    text: String,
    keyboard: Vec<Vec<InlineButton>>,
}

#[derive(Default)]
struct MockBot {
    sent: Mutex<Vec<MockSentMessage>>,
    callbacks: Mutex<Vec<String>>,
    edited: Mutex<Vec<String>>,
    next_message_id: AtomicI64,
    fail_next_send_permanently: std::sync::atomic::AtomicBool,
    updates_notified: Notify,
}

struct FixedClock(RwLock<OffsetDateTime>);

impl FixedClock {
    fn new(now: OffsetDateTime) -> Self {
        Self(RwLock::new(now))
    }

    fn set(&self, now: OffsetDateTime) {
        if let Ok(mut current) = self.0.write() {
            *current = now;
        }
    }
}

impl Clock for FixedClock {
    fn now(&self) -> OffsetDateTime {
        self.0
            .read()
            .map(|current| *current)
            .unwrap_or_else(|poisoned| *poisoned.into_inner())
    }
}

#[async_trait]
impl BotClient for MockBot {
    async fn get_me(&self) -> Result<TelegramUser, hako_auth::telegram::BotError> {
        Ok(TelegramUser {
            id: 1,
            username: Some("hako_test_bot".to_owned()),
        })
    }

    async fn get_updates(
        &self,
        _offset: i64,
        _timeout_seconds: u64,
    ) -> Result<Vec<TelegramUpdate>, hako_auth::telegram::BotError> {
        self.updates_notified.notify_one();
        Ok(Vec::new())
    }

    async fn send_message(
        &self,
        chat_id: i64,
        text: &str,
        keyboard: &[Vec<InlineButton>],
    ) -> Result<SentMessage, hako_auth::telegram::BotError> {
        if self
            .fail_next_send_permanently
            .swap(false, Ordering::SeqCst)
        {
            return Err(BotError::Permanent);
        }
        let message_id = self.next_message_id.fetch_add(1, Ordering::Relaxed) + 1;
        let sent = MockSentMessage {
            chat_id,
            message_id,
            text: text.to_owned(),
            keyboard: keyboard.to_vec(),
        };
        if let Ok(mut messages) = self.sent.lock() {
            messages.push(sent);
        }
        Ok(SentMessage {
            chat_id,
            message_id,
        })
    }

    async fn edit_message_text(
        &self,
        _chat_id: i64,
        _message_id: i64,
        text: &str,
    ) -> Result<(), hako_auth::telegram::BotError> {
        if let Ok(mut messages) = self.edited.lock() {
            messages.push(text.to_owned());
        }
        Ok(())
    }

    async fn answer_callback_query(
        &self,
        callback_query_id: &str,
        _text: Option<&str>,
    ) -> Result<(), hako_auth::telegram::BotError> {
        if let Ok(mut callbacks) = self.callbacks.lock() {
            callbacks.push(callback_query_id.to_owned());
        }
        Ok(())
    }
}

#[tokio::test]
async fn postgres_auth_flow_and_telegram_updates() {
    let Ok(database_url) = env::var("HAKO_TEST_DATABASE_URL") else {
        println!(
            "SKIP: HAKO_TEST_DATABASE_URL is unset; PostgreSQL integration coverage was not run."
        );
        return;
    };

    let pool = PgPoolOptions::new()
        .max_connections(5)
        .connect(&database_url)
        .await
        .expect("connect HAKO_TEST_DATABASE_URL");
    sqlx::migrate!("./migrations")
        .run(&pool)
        .await
        .expect("apply auth migrations to the empty test database");

    let current_time = OffsetDateTime::now_utc();
    let base = OffsetDateTime::from_unix_timestamp_nanos(
        current_time.unix_timestamp_nanos() / 1_000 * 1_000,
    )
    .expect("truncate test clock to PostgreSQL microsecond precision");
    let clock = Arc::new(FixedClock::new(base));
    let shared_clock: SharedClock = clock.clone();
    let auth = AuthService::new(pool.clone(), shared_clock);
    let public_api = public_router(test_app_state(auth.clone()));
    let bot = MockBot::default();
    let telegram_id =
        6_000_000_000_000_000 + base.unix_timestamp() * 1_000 + i64::from(std::process::id());
    let username = test_username();
    let telegram_user = TelegramUser {
        id: telegram_id,
        username: Some(username.clone()),
    };
    let update_ids =
        AtomicI64::new(base.unix_timestamp() * 10_000 + i64::from(std::process::id()) * 10_000);

    // Register, approve through /start, exchange once, and fetch /me.
    let (invalid_status, invalid_body): (StatusCode, Value) = api_json(
        &public_api,
        "POST",
        "/v1/auth/register",
        None,
        Some(r#"{"device_name":" "}"#),
    )
    .await;
    assert_eq!(invalid_status, StatusCode::BAD_REQUEST);
    assert!(invalid_body.get("error").is_some());

    let (register_status, registration): (StatusCode, RegisterResponse) = api_json(
        &public_api,
        "POST",
        "/v1/auth/register",
        None,
        Some(r#"{"device_name":"Test PC"}"#),
    )
    .await;
    assert_eq!(register_status, StatusCode::CREATED);
    let code = deep_link_code(&registration);
    let start_update = message_update(&update_ids, telegram_user.clone(), "/start", Some(&code));
    auth.process_update(&start_update)
        .await
        .expect("process registration start");
    drain_outbox(&auth, &bot).await;
    let (status_code, request_status): (StatusCode, Value) = api_json(
        &public_api,
        "GET",
        &format!("/v1/auth/requests/{}", registration.request_id),
        Some(&registration.exchange_secret),
        None,
    )
    .await;
    assert_eq!(status_code, StatusCode::OK);
    assert_eq!(request_status["status"], "approved");
    let (exchange_status, initial): (StatusCode, ExchangeResponse) = api_json(
        &public_api,
        "POST",
        "/v1/auth/exchange",
        Some(&registration.exchange_secret),
        Some(&format!(
            "{{\"request_id\":\"{}\"}}",
            registration.request_id
        )),
    )
    .await;
    assert_eq!(exchange_status, StatusCode::OK);
    let (repeat_status, repeat_body): (StatusCode, Value) = api_json(
        &public_api,
        "POST",
        "/v1/auth/exchange",
        Some(&registration.exchange_secret),
        Some(&format!(
            "{{\"request_id\":\"{}\"}}",
            registration.request_id
        )),
    )
    .await;
    assert_eq!(repeat_status, StatusCode::CONFLICT);
    assert!(repeat_body.get("error").is_some());
    let (consumed_status, consumed_body): (StatusCode, Value) = api_json(
        &public_api,
        "GET",
        &format!("/v1/auth/requests/{}", registration.request_id),
        Some(&registration.exchange_secret),
        None,
    )
    .await;
    assert_eq!(consumed_status, StatusCode::OK);
    assert_eq!(consumed_body["status"], "consumed");
    let (me_status, me): (StatusCode, hako_auth::service::MeResponse) = api_json(
        &public_api,
        "GET",
        "/v1/auth/me",
        Some(&initial.token),
        None,
    )
    .await;
    assert_eq!(me_status, StatusCode::OK);
    assert_eq!(me.account.username, username);
    assert_eq!(me.session.device_name, "Test PC");

    // An exchange secret is specific to one request.
    let isolated = auth
        .register("Other device", "hako_test_bot")
        .await
        .expect("create isolated request");
    assert!(auth
        .request_status(registration.request_id, &isolated.exchange_secret)
        .await
        .is_err());

    // The session is valid just before its exact sliding expiry and renews from that time.
    let initial_hash = hash(initial.token.as_bytes());
    let initial_expiry = session_expiry(&pool, &initial_hash).await;
    clock.set(initial_expiry - Duration::microseconds(1));
    auth.me(&initial.token)
        .await
        .expect("session is alive one microsecond before expiry");
    assert_eq!(
        session_expiry(&pool, &initial_hash).await,
        initial_expiry - Duration::microseconds(1) + Duration::days(14)
    );

    // A second session is dead just after its initial expiry and cannot be renewed.
    clock.set(base);
    let boundary_live = approve_login(
        &auth,
        &bot,
        &update_ids,
        telegram_user.clone(),
        "Boundary PC",
    )
    .await;
    let boundary_hash = hash(boundary_live.token.as_bytes());
    let boundary_expiry = session_expiry(&pool, &boundary_hash).await;
    clock.set(boundary_expiry + Duration::microseconds(1));
    assert!(auth.me(&boundary_live.token).await.is_err());
    assert_eq!(session_expiry(&pool, &boundary_hash).await, boundary_expiry);
    clock.set(base);

    // Internal token checks also slide the expiry.
    let internal_session = approve_login(
        &auth,
        &bot,
        &update_ids,
        telegram_user.clone(),
        "Internal check PC",
    )
    .await;
    clock.set(base + Duration::minutes(1));
    let internal = auth
        .check_token(&internal_session.token)
        .await
        .expect("internal token check");
    assert_eq!(internal.username, username);
    assert_eq!(
        session_expiry(&pool, &hash(internal_session.token.as_bytes())).await,
        base + Duration::minutes(1) + Duration::days(14)
    );
    let state = AppState::new(
        pool.clone(),
        "test-service-secret-value".to_owned(),
        Vec::new(),
        Arc::new(tokio::sync::RwLock::new(Some("hako_test_bot".to_owned()))),
    );
    let response = internal_router(state)
        .oneshot(
            Request::builder()
                .method("POST")
                .uri("/internal/v1/tokens/check")
                .header("content-type", "application/json")
                .header("X-Service-Secret", "wrong-secret")
                .body(Body::from(r#"{"token":"not-used"}"#))
                .expect("build internal request"),
        )
        .await
        .expect("run internal router");
    assert_eq!(response.status(), StatusCode::UNAUTHORIZED);
    clock.set(base);

    // A username change from the same Telegram identity updates the account.
    let changed_username = test_username();
    let rename_update = message_update(
        &update_ids,
        TelegramUser {
            id: telegram_id,
            username: Some(changed_username.clone()),
        },
        "/unrelated",
        None,
    );
    auth.process_update(&rename_update)
        .await
        .expect("process username change");
    let stored_username =
        sqlx::query_scalar::<_, String>("SELECT username FROM accounts WHERE telegram_id = $1")
            .bind(telegram_id)
            .fetch_one(&pool)
            .await
            .expect("read changed username");
    assert_eq!(stored_username, changed_username);
    let (unknown_login_status, unknown_login_body): (StatusCode, Value) = api_json(
        &public_api,
        "POST",
        "/v1/auth/login",
        None,
        Some(&format!(
            "{{\"username\":\"{username}\",\"device_name\":\"Old name\"}}"
        )),
    )
    .await;
    assert_eq!(unknown_login_status, StatusCode::NOT_FOUND);
    assert!(unknown_login_body.get("error").is_some());

    // Reusing the released username resolves to the new Telegram identity, not the old account.
    clock.set(base + Duration::seconds(1));
    let reassigned_registration = auth
        .register("Reassigned name", "hako_test_bot")
        .await
        .expect("create registration for reassigned name");
    let reassigned_user = TelegramUser {
        id: telegram_id + 2,
        username: Some(username.clone()),
    };
    let reassigned_start = message_update(
        &update_ids,
        reassigned_user.clone(),
        "/start",
        Some(&deep_link_code(&reassigned_registration)),
    );
    auth.process_update(&reassigned_start)
        .await
        .expect("register reassigned username");
    drain_outbox(&auth, &bot).await;
    let reassigned_login = auth
        .login(&username, "Reassigned username login")
        .await
        .expect("login resolves reassigned username");
    let resolved_telegram_id = sqlx::query_scalar::<_, i64>(
        "SELECT a.telegram_id FROM auth_requests ar JOIN accounts a ON a.id = ar.account_id WHERE ar.id = $1",
    )
    .bind(reassigned_login.request_id)
    .fetch_one(&pool)
    .await
    .expect("resolved account identity");
    assert_eq!(resolved_telegram_id, telegram_id + 2);
    drain_outbox(&auth, &bot).await;
    let reassigned_message = latest_login_message(&bot);
    let old_identity_callback = callback_update(
        &update_ids,
        TelegramUser {
            id: telegram_id,
            username: Some(changed_username.clone()),
        },
        &reassigned_message,
        &reassigned_message.keyboard[0][0].callback_data,
        "old-identity-callback",
    );
    auth.process_update(&old_identity_callback)
        .await
        .expect("reject callback from prior username owner");
    drain_outbox(&auth, &bot).await;
    assert_eq!(
        auth.request_status(
            reassigned_login.request_id,
            &reassigned_login.exchange_secret
        )
        .await
        .expect("reassigned request remains pending")
        .status,
        "pending"
    );
    let reassigned_deny = callback_update(
        &update_ids,
        reassigned_user,
        &reassigned_message,
        &reassigned_message.keyboard[0][1].callback_data,
        "reassigned-deny-callback",
    );
    auth.process_update(&reassigned_deny)
        .await
        .expect("new username owner controls the request");
    drain_outbox(&auth, &bot).await;
    clock.set(base);

    // Login denial works; an unrelated Telegram user cannot change the request.
    let (denied_login_status, denied_login): (StatusCode, hako_auth::service::LoginResponse) =
        api_json(
            &public_api,
            "POST",
            "/v1/auth/login",
            None,
            Some(&format!(
                "{{\"username\":\"{changed_username}\",\"device_name\":\"Denied PC\"}}"
            )),
        )
        .await;
    assert_eq!(denied_login_status, StatusCode::CREATED);
    drain_outbox(&auth, &bot).await;
    let login_message = latest_login_message(&bot);
    let callback_data = login_message.keyboard[0][0].callback_data.clone();
    let foreign_callback = callback_update(
        &update_ids,
        TelegramUser {
            id: telegram_id + 99,
            username: Some(changed_username.clone()),
        },
        &login_message,
        &callback_data,
        "foreign-callback",
    );
    auth.process_update(&foreign_callback)
        .await
        .expect("process unauthorized callback");
    drain_outbox(&auth, &bot).await;
    assert_eq!(
        auth.request_status(denied_login.request_id, &denied_login.exchange_secret)
            .await
            .expect("status after foreign callback")
            .status,
        "pending"
    );
    let deny_data = login_message.keyboard[0][1].callback_data.clone();
    let deny_update = callback_update(
        &update_ids,
        TelegramUser {
            id: telegram_id,
            username: Some(changed_username.clone()),
        },
        &login_message,
        &deny_data,
        "deny-callback",
    );
    auth.process_update(&deny_update)
        .await
        .expect("process login denial");
    drain_outbox(&auth, &bot).await;
    assert_eq!(
        auth.request_status(denied_login.request_id, &denied_login.exchange_secret)
            .await
            .expect("denied status")
            .status,
        "denied"
    );
    assert!(auth
        .exchange(denied_login.request_id, &denied_login.exchange_secret)
        .await
        .is_err());

    // Duplicate updates are processed once; repeated registration reports account_exists.
    let duplicate = message_update(
        &update_ids,
        TelegramUser {
            id: telegram_id,
            username: Some(changed_username.clone()),
        },
        "/sessions",
        None,
    );
    auth.process_update(&duplicate)
        .await
        .expect("first duplicate candidate");
    drain_outbox(&auth, &bot).await;
    let sent_count = bot.sent.lock().expect("mock bot lock").len();
    auth.process_update(&duplicate)
        .await
        .expect("replayed duplicate update");
    drain_outbox(&auth, &bot).await;
    assert_eq!(bot.sent.lock().expect("mock bot lock").len(), sent_count);

    let repeat_registration = auth
        .register("Repeated", "hako_test_bot")
        .await
        .expect("create repeated registration request");
    let repeat_update = message_update(
        &update_ids,
        TelegramUser {
            id: telegram_id,
            username: Some(changed_username.clone()),
        },
        "/start",
        Some(&deep_link_code(&repeat_registration)),
    );
    auth.process_update(&repeat_update)
        .await
        .expect("process repeat registration");
    drain_outbox(&auth, &bot).await;
    assert_eq!(
        auth.request_status(
            repeat_registration.request_id,
            &repeat_registration.exchange_secret
        )
        .await
        .expect("repeat registration status")
        .status,
        "account_exists"
    );

    // An expired registration request is closed at the five-minute boundary.
    let expired_registration = auth
        .register("Expired", "hako_test_bot")
        .await
        .expect("create expiring request");
    clock.set(base + Duration::minutes(5));
    let expired_start = message_update(
        &update_ids,
        TelegramUser {
            id: telegram_id + 1,
            username: Some(test_username()),
        },
        "/start",
        Some(&deep_link_code(&expired_registration)),
    );
    auth.process_update(&expired_start)
        .await
        .expect("process expired request");
    assert_eq!(
        auth.request_status(
            expired_registration.request_id,
            &expired_registration.exchange_secret
        )
        .await
        .expect("expired request status")
        .status,
        "expired"
    );
    clock.set(base);

    // Bot revocation, logout, and revoke-all also invalidate pending approvals.
    let bot_revoked = approve_login(
        &auth,
        &bot,
        &update_ids,
        TelegramUser {
            id: telegram_id,
            username: Some(changed_username.clone()),
        },
        "Bot revoke PC",
    )
    .await;
    let sessions_update = message_update(
        &update_ids,
        TelegramUser {
            id: telegram_id,
            username: Some(changed_username.clone()),
        },
        "/sessions",
        None,
    );
    auth.process_update(&sessions_update)
        .await
        .expect("request session list");
    drain_outbox(&auth, &bot).await;
    let session_id = session_id_for_token(&pool, &bot_revoked.token).await;
    let revoke_data = format!("session:revoke:{session_id}");
    let sessions_message = latest_sessions_message(&bot);
    let revoke_update = callback_update(
        &update_ids,
        TelegramUser {
            id: telegram_id,
            username: Some(changed_username.clone()),
        },
        &sessions_message,
        &revoke_data,
        "revoke-session",
    );
    auth.process_update(&revoke_update)
        .await
        .expect("revoke session through bot");
    drain_outbox(&auth, &bot).await;
    assert!(auth.me(&bot_revoked.token).await.is_err());

    let logged_out = approve_login(
        &auth,
        &bot,
        &update_ids,
        TelegramUser {
            id: telegram_id,
            username: Some(changed_username.clone()),
        },
        "Logout PC",
    )
    .await;
    let logout_response = public_api
        .clone()
        .oneshot(
            Request::builder()
                .method("POST")
                .uri("/v1/auth/logout")
                .header("Authorization", format!("Bearer {}", logged_out.token))
                .body(Body::empty())
                .expect("build logout request"),
        )
        .await
        .expect("run logout route");
    assert_eq!(logout_response.status(), StatusCode::NO_CONTENT);
    assert!(auth.me(&logged_out.token).await.is_err());

    let revoke_all_session = approve_login(
        &auth,
        &bot,
        &update_ids,
        TelegramUser {
            id: telegram_id,
            username: Some(changed_username.clone()),
        },
        "Revoke all PC",
    )
    .await;
    let pending = auth
        .login(&changed_username, "Waiting approval")
        .await
        .expect("create pending login for revoke-all");
    drain_outbox(&auth, &bot).await;
    for _ in 0..32 {
        sqlx::query(
            "INSERT INTO sessions \
             (id, account_id, token_hash, device_name, created_at, last_used_at, expires_at) \
             VALUES ($1, $2, $3, $4, $5, $5, $6)",
        )
        .bind(Uuid::new_v4())
        .bind(initial.account.id)
        .bind(hash(Uuid::new_v4().to_string().as_bytes()))
        .bind("x".repeat(128))
        .bind(base)
        .bind(base + Duration::days(14))
        .execute(&pool)
        .await
        .expect("insert long-name sessions");
    }
    let sessions_update = message_update(
        &update_ids,
        TelegramUser {
            id: telegram_id,
            username: Some(changed_username.clone()),
        },
        "/sessions",
        None,
    );
    auth.process_update(&sessions_update)
        .await
        .expect("load sessions before revoke-all");
    drain_outbox(&auth, &bot).await;
    let sessions_message = latest_sessions_message(&bot);
    assert!(sessions_message.text.encode_utf16().count() <= 4096);
    assert!(sessions_message.text.contains("ещё"));
    assert!(sessions_message.keyboard.len() <= 21);
    let revoke_all = callback_update(
        &update_ids,
        TelegramUser {
            id: telegram_id,
            username: Some(changed_username.clone()),
        },
        &sessions_message,
        "sessions:revoke_all",
        "revoke-all",
    );
    auth.process_update(&revoke_all)
        .await
        .expect("revoke all through bot");
    drain_outbox(&auth, &bot).await;
    assert!(auth.me(&revoke_all_session.token).await.is_err());
    assert_eq!(
        auth.request_status(pending.request_id, &pending.exchange_secret)
            .await
            .expect("pending request after revoke-all")
            .status,
        "expired"
    );
    assert!(auth
        .active_sessions(telegram_id)
        .await
        .expect("active sessions after revoke-all")
        .is_empty());

    // An approved authorization remains revocable when no session was exchanged yet.
    let approved_unexchanged = auth
        .login(&changed_username, "Approved but not exchanged")
        .await
        .expect("create approved unexchanged login");
    drain_outbox(&auth, &bot).await;
    let login_message = latest_login_message(&bot);
    let approve_unexchanged = callback_update(
        &update_ids,
        TelegramUser {
            id: telegram_id,
            username: Some(changed_username.clone()),
        },
        &login_message,
        &login_message.keyboard[0][0].callback_data,
        "approve-unexchanged",
    );
    auth.process_update(&approve_unexchanged)
        .await
        .expect("approve unexchanged login");
    drain_outbox(&auth, &bot).await;
    assert_eq!(
        auth.request_status(
            approved_unexchanged.request_id,
            &approved_unexchanged.exchange_secret
        )
        .await
        .expect("approved unexchanged request status")
        .status,
        "approved"
    );
    let no_session_update = message_update(
        &update_ids,
        TelegramUser {
            id: telegram_id,
            username: Some(changed_username.clone()),
        },
        "/sessions",
        None,
    );
    auth.process_update(&no_session_update)
        .await
        .expect("list sessions with pending authorization");
    drain_outbox(&auth, &bot).await;
    let no_session_message = latest_sessions_message(&bot);
    assert!(no_session_message.text.contains("Активных сессий нет"));
    assert!(no_session_message
        .keyboard
        .iter()
        .flatten()
        .any(|button| button.callback_data == "sessions:revoke_all"));
    let revoke_unexchanged = callback_update(
        &update_ids,
        TelegramUser {
            id: telegram_id,
            username: Some(changed_username.clone()),
        },
        &no_session_message,
        "sessions:revoke_all",
        "revoke-unexchanged",
    );
    auth.process_update(&revoke_unexchanged)
        .await
        .expect("revoke unexchanged authorization");
    drain_outbox(&auth, &bot).await;
    assert_eq!(
        auth.request_status(
            approved_unexchanged.request_id,
            &approved_unexchanged.exchange_secret
        )
        .await
        .expect("revoked unexchanged request status")
        .status,
        "expired"
    );
    assert!(auth
        .exchange(
            approved_unexchanged.request_id,
            &approved_unexchanged.exchange_secret
        )
        .await
        .is_err());

    // Exercise the exchange/revoke race while the session insert is held in a trigger.
    let race_login = auth
        .login(&changed_username, "Concurrent revoke PC")
        .await
        .expect("create concurrent-revoke login");
    drain_outbox(&auth, &bot).await;
    let race_message = latest_login_message(&bot);
    let race_approve = callback_update(
        &update_ids,
        TelegramUser {
            id: telegram_id,
            username: Some(changed_username.clone()),
        },
        &race_message,
        &race_message.keyboard[0][0].callback_data,
        "approve-concurrent-revoke",
    );
    auth.process_update(&race_approve)
        .await
        .expect("approve concurrent-revoke login");
    drain_outbox(&auth, &bot).await;

    const INSERT_GATE_KEY: i64 = -7_000_000_000_000_001;
    sqlx::query("DROP TRIGGER IF EXISTS hako_test_hold_session_insert ON sessions")
        .execute(&pool)
        .await
        .expect("remove prior test trigger");
    sqlx::query("DROP FUNCTION IF EXISTS hako_test_hold_session_insert()")
        .execute(&pool)
        .await
        .expect("remove prior test trigger function");
    sqlx::query(
        "CREATE FUNCTION hako_test_hold_session_insert() RETURNS trigger LANGUAGE plpgsql AS $$ \
         BEGIN PERFORM pg_advisory_xact_lock(-7000000000000001); RETURN NEW; END $$",
    )
    .execute(&pool)
    .await
    .expect("install session insert gate function");
    sqlx::query(
        "CREATE TRIGGER hako_test_hold_session_insert BEFORE INSERT ON sessions \
         FOR EACH ROW EXECUTE FUNCTION hako_test_hold_session_insert()",
    )
    .execute(&pool)
    .await
    .expect("install session insert gate");
    let mut gate_connection = pool.acquire().await.expect("acquire test gate connection");
    sqlx::query("SELECT pg_advisory_lock($1)")
        .bind(INSERT_GATE_KEY)
        .execute(&mut *gate_connection)
        .await
        .expect("hold test gate lock");

    let exchange_auth = auth.clone();
    let exchange_id = race_login.request_id;
    let exchange_secret = race_login.exchange_secret.clone();
    let exchange_task =
        tokio::spawn(async move { exchange_auth.exchange(exchange_id, &exchange_secret).await });
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            let blocked = sqlx::query_scalar::<_, bool>(
                "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() \
                 AND pid <> pg_backend_pid() AND query LIKE '%INSERT INTO sessions%' \
                 AND wait_event_type = 'Lock' AND wait_event = 'advisory')",
            )
            .fetch_one(&pool)
            .await
            .expect("observe blocked exchange");
            if blocked {
                break;
            }
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
    })
    .await
    .expect("exchange reached its gated session insert");

    let revoke_auth = auth.clone();
    let revoke_task = tokio::spawn(async move { revoke_auth.revoke_all(telegram_id).await });
    tokio::time::timeout(std::time::Duration::from_secs(5), async {
        loop {
            let active = sqlx::query_scalar::<_, bool>(
                "SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE datname = current_database() \
                 AND pid <> pg_backend_pid() \
                 AND (query LIKE '%UPDATE sessions SET revoked_at%' \
                      OR query LIKE '%UPDATE auth_requests SET status%' \
                      OR query LIKE '%pg_advisory_xact_lock%'))",
            )
            .fetch_one(&pool)
            .await
            .expect("observe revoke-all transaction");
            if active {
                break;
            }
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
    })
    .await
    .expect("revoke-all started while exchange is gated");
    sqlx::query("SELECT pg_advisory_unlock($1)")
        .bind(INSERT_GATE_KEY)
        .execute(&mut *gate_connection)
        .await
        .expect("release test gate lock");
    drop(gate_connection);
    let exchange_result = exchange_task.await.expect("join exchange");
    assert!(revoke_task
        .await
        .expect("join revoke-all")
        .expect("revoke-all succeeds"));
    if let Ok(response) = exchange_result {
        assert!(auth.me(&response.token).await.is_err());
    }
    let live_race_sessions = sqlx::query_scalar::<_, i64>(
        "SELECT count(*) FROM sessions WHERE account_id = $1 AND revoked_at IS NULL AND expires_at > $2",
    )
    .bind(initial.account.id)
    .bind(base)
    .fetch_one(&pool)
    .await
    .expect("count sessions after concurrent revoke-all");
    assert_eq!(live_race_sessions, 0);
    sqlx::query("DROP TRIGGER hako_test_hold_session_insert ON sessions")
        .execute(&pool)
        .await
        .expect("remove session insert test trigger");
    sqlx::query("DROP FUNCTION hako_test_hold_session_insert()")
        .execute(&pool)
        .await
        .expect("remove session insert test function");

    // A permanent Telegram delivery failure is isolated before polling continues.
    let rejected_message = format!("reject-{}", Uuid::new_v4());
    let delivered_message = format!("deliver-{}", Uuid::new_v4());
    for text in [&rejected_message, &delivered_message] {
        let payload = json!({
            "kind": "send_message",
            "chat_id": telegram_id,
            "text": text,
            "keyboard": [],
            "auth_request_id": null
        });
        sqlx::query("INSERT INTO telegram_outbox (payload, created_at) VALUES ($1, $2)")
            .bind(payload)
            .bind(base)
            .execute(&pool)
            .await
            .expect("enqueue worker test message");
    }
    let worker_bot = Arc::new(MockBot::default());
    worker_bot
        .fail_next_send_permanently
        .store(true, Ordering::SeqCst);
    let updates_observed = worker_bot.updates_notified.notified();
    let worker = tokio::spawn(run_bot_worker(
        auth.clone(),
        worker_bot.clone(),
        Arc::new(tokio::sync::RwLock::new(None)),
    ));
    tokio::time::timeout(std::time::Duration::from_secs(5), updates_observed)
        .await
        .expect("bot worker should resume getUpdates after skipping a rejected message");
    worker.abort();
    let _ = worker.await;
    let rejected_failed = sqlx::query_scalar::<_, bool>(
        "SELECT failed_at IS NOT NULL FROM telegram_outbox WHERE payload->>'text' = $1",
    )
    .bind(&rejected_message)
    .fetch_one(&pool)
    .await
    .expect("read rejected message status");
    let delivered_at = sqlx::query_scalar::<_, Option<OffsetDateTime>>(
        "SELECT delivered_at FROM telegram_outbox WHERE payload->>'text' = $1",
    )
    .bind(&delivered_message)
    .fetch_one(&pool)
    .await
    .expect("read following message status");
    assert!(rejected_failed);
    assert!(delivered_at.is_some());
    assert!(worker_bot
        .sent
        .lock()
        .expect("worker mock bot lock")
        .iter()
        .any(|message| message.text == delivered_message));

    pool.close().await;
}

fn test_app_state(auth: AuthService) -> AppState {
    AppState {
        auth,
        bot_username: Arc::new(tokio::sync::RwLock::new(Some("hako_test_bot".to_owned()))),
        service_secret: Arc::from("test-service-secret-value"),
        trusted_proxies: Arc::new(Vec::new()),
        rate_limiter: Arc::new(Mutex::new(RateLimiter::default())),
    }
}

async fn api_json<T: DeserializeOwned>(
    app: &Router,
    method: &str,
    uri: &str,
    bearer: Option<&str>,
    body: Option<&str>,
) -> (StatusCode, T) {
    let mut builder = Request::builder().method(method).uri(uri);
    if let Some(bearer) = bearer {
        builder = builder.header("Authorization", format!("Bearer {bearer}"));
    }
    if body.is_some() {
        builder = builder.header("content-type", "application/json");
    }
    let request = builder
        .body(
            body.map(|body| Body::from(body.to_owned()))
                .unwrap_or_else(Body::empty),
        )
        .expect("build API request");
    let response = app.clone().oneshot(request).await.expect("run API router");
    let status = response.status();
    let bytes = axum::body::to_bytes(response.into_body(), 16 * 1024)
        .await
        .expect("read API response");
    let body = serde_json::from_slice(&bytes).expect("decode API JSON response");
    (status, body)
}

async fn approve_login(
    auth: &AuthService,
    bot: &MockBot,
    update_ids: &AtomicI64,
    user: TelegramUser,
    device_name: &str,
) -> ExchangeResponse {
    let login = auth
        .login(
            user.username.as_deref().expect("Telegram username"),
            device_name,
        )
        .await
        .expect("create login request");
    drain_outbox(auth, bot).await;
    let login_message = latest_login_message(bot);
    let approve_data = login_message.keyboard[0][0].callback_data.clone();
    let callback = callback_update(
        update_ids,
        user,
        &login_message,
        &approve_data,
        &format!("approve-{}", login.request_id),
    );
    auth.process_update(&callback)
        .await
        .expect("process login approval");
    drain_outbox(auth, bot).await;
    auth.exchange(login.request_id, &login.exchange_secret)
        .await
        .expect("exchange approved login")
}

async fn drain_outbox(auth: &AuthService, bot: &MockBot) {
    for _ in 0..500 {
        if !auth
            .drain_outbox(bot)
            .await
            .expect("deliver Telegram outbox event")
        {
            return;
        }
    }
    panic!("Telegram outbox did not drain");
}

fn message_update(
    update_ids: &AtomicI64,
    user: TelegramUser,
    command: &str,
    argument: Option<&str>,
) -> TelegramUpdate {
    let text = match argument {
        Some(argument) => format!("{command} {argument}"),
        None => command.to_owned(),
    };
    TelegramUpdate {
        update_id: update_ids.fetch_add(1, Ordering::Relaxed),
        message: Some(TelegramMessage {
            message_id: update_ids.fetch_add(1, Ordering::Relaxed),
            from: Some(user.clone()),
            chat: TelegramChat {
                id: user.id,
                kind: "private".to_owned(),
            },
            text: Some(text),
        }),
        callback_query: None,
    }
}

fn callback_update(
    update_ids: &AtomicI64,
    user: TelegramUser,
    origin: &MockSentMessage,
    data: &str,
    callback_id: &str,
) -> TelegramUpdate {
    TelegramUpdate {
        update_id: update_ids.fetch_add(1, Ordering::Relaxed),
        message: None,
        callback_query: Some(TelegramCallbackQuery {
            id: callback_id.to_owned(),
            from: user,
            message: Some(TelegramMessage {
                message_id: origin.message_id,
                from: None,
                chat: TelegramChat {
                    id: origin.chat_id,
                    kind: "private".to_owned(),
                },
                text: Some(origin.text.clone()),
            }),
            data: Some(data.to_owned()),
        }),
    }
}

fn deep_link_code(registration: &RegisterResponse) -> String {
    registration
        .bot_link
        .split_once("?start=")
        .map(|(_, code)| code.to_owned())
        .expect("registration deep link")
}

fn latest_login_message(bot: &MockBot) -> MockSentMessage {
    bot.sent
        .lock()
        .expect("mock bot lock")
        .iter()
        .rev()
        .find(|message| message.text.starts_with("Запрос на вход"))
        .cloned()
        .expect("login notification")
}

fn latest_sessions_message(bot: &MockBot) -> MockSentMessage {
    bot.sent
        .lock()
        .expect("mock bot lock")
        .iter()
        .rev()
        .find(|message| {
            message.text.starts_with("Активные сессии:")
                || message.text.starts_with("Активных сессий нет.")
        })
        .cloned()
        .expect("sessions list")
}

fn test_username() -> String {
    format!("u{}", &Uuid::new_v4().simple().to_string()[..30])
}

async fn session_expiry(pool: &PgPool, token_hash: &[u8]) -> OffsetDateTime {
    sqlx::query_scalar::<_, OffsetDateTime>("SELECT expires_at FROM sessions WHERE token_hash = $1")
        .bind(token_hash)
        .fetch_one(pool)
        .await
        .expect("session expiry")
}

async fn session_id_for_token(pool: &PgPool, token: &str) -> Uuid {
    sqlx::query_scalar::<_, Uuid>("SELECT id FROM sessions WHERE token_hash = $1")
        .bind(hash(token.as_bytes()))
        .fetch_one(pool)
        .await
        .expect("session ID")
}
