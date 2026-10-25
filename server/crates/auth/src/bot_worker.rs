use std::{sync::Arc, time::Duration};

use tokio::sync::RwLock;

use crate::{service::AuthService, telegram::BotClient};

pub async fn run_bot_worker(
    auth: AuthService,
    bot: Arc<dyn BotClient>,
    bot_username: Arc<RwLock<Option<String>>>,
) {
    let mut backoff = Duration::from_secs(1);
    loop {
        let username = match bot.get_me().await {
            Ok(user) => user.username,
            Err(_) => {
                tracing::warn!("Telegram Bot API unavailable; retrying getMe");
                tokio::time::sleep(backoff).await;
                backoff = grow_backoff(backoff);
                continue;
            }
        };
        let Some(username) = username else {
            tracing::warn!("Telegram bot has no username; retrying getMe");
            tokio::time::sleep(backoff).await;
            backoff = grow_backoff(backoff);
            continue;
        };
        *bot_username.write().await = Some(username.trim_start_matches('@').to_owned());
        tracing::info!("Telegram bot identity is ready");
        backoff = Duration::from_secs(1);

        loop {
            let offset = match auth.telegram_offset().await {
                Ok(offset) => offset,
                Err(_) => {
                    tracing::warn!("failed to read Telegram update offset; retrying");
                    tokio::time::sleep(backoff).await;
                    backoff = grow_backoff(backoff);
                    continue;
                }
            };

            let mut delivery_failed = false;
            loop {
                match auth.drain_outbox(bot.as_ref()).await {
                    Ok(true) => {}
                    Ok(false) => break,
                    Err(_) => {
                        tracing::warn!("Telegram delivery failed; retrying");
                        delivery_failed = true;
                        break;
                    }
                }
            }
            if delivery_failed {
                tokio::time::sleep(backoff).await;
                backoff = grow_backoff(backoff);
                continue;
            }

            let updates = match bot.get_updates(offset, 30).await {
                Ok(updates) => updates,
                Err(_) => {
                    tracing::warn!("Telegram polling failed; retrying");
                    tokio::time::sleep(backoff).await;
                    backoff = grow_backoff(backoff);
                    continue;
                }
            };
            let mut update_failed = false;
            for update in &updates {
                if auth.process_update(update).await.is_err() {
                    tracing::warn!("failed to process Telegram update; retrying");
                    update_failed = true;
                    break;
                }
            }
            if update_failed {
                tokio::time::sleep(backoff).await;
                backoff = grow_backoff(backoff);
                continue;
            }
            backoff = Duration::from_secs(1);
        }
    }
}

fn grow_backoff(current: Duration) -> Duration {
    (current * 2).min(Duration::from_secs(60))
}
