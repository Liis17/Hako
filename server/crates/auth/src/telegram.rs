use async_trait::async_trait;
use reqwest::Client;
use serde::{de::DeserializeOwned, Deserialize, Serialize};
use serde_json::{json, Value};

#[derive(Clone, Debug, Deserialize)]
pub struct TelegramUser {
    pub id: i64,
    pub username: Option<String>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct TelegramChat {
    pub id: i64,
    #[serde(rename = "type")]
    pub kind: String,
}

#[derive(Clone, Debug, Deserialize)]
pub struct TelegramMessage {
    pub message_id: i64,
    pub from: Option<TelegramUser>,
    pub chat: TelegramChat,
    pub text: Option<String>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct TelegramCallbackQuery {
    pub id: String,
    pub from: TelegramUser,
    pub message: Option<TelegramMessage>,
    pub data: Option<String>,
}

#[derive(Clone, Debug, Deserialize)]
pub struct TelegramUpdate {
    pub update_id: i64,
    pub message: Option<TelegramMessage>,
    pub callback_query: Option<TelegramCallbackQuery>,
}

#[derive(Clone, Debug, Serialize, Deserialize)]
pub struct InlineButton {
    pub text: String,
    pub callback_data: String,
}

#[derive(Clone, Copy, Debug)]
pub struct SentMessage {
    pub chat_id: i64,
    pub message_id: i64,
}

#[derive(Debug)]
pub struct BotError;

#[async_trait]
pub trait BotClient: Send + Sync {
    async fn get_me(&self) -> Result<TelegramUser, BotError>;
    async fn get_updates(
        &self,
        offset: i64,
        timeout_seconds: u64,
    ) -> Result<Vec<TelegramUpdate>, BotError>;
    async fn send_message(
        &self,
        chat_id: i64,
        text: &str,
        keyboard: &[Vec<InlineButton>],
    ) -> Result<SentMessage, BotError>;
    async fn edit_message_text(
        &self,
        chat_id: i64,
        message_id: i64,
        text: &str,
    ) -> Result<(), BotError>;
    async fn answer_callback_query(
        &self,
        callback_query_id: &str,
        text: Option<&str>,
    ) -> Result<(), BotError>;
}

pub struct ReqwestBotClient {
    token: String,
    client: Client,
}

impl ReqwestBotClient {
    pub fn new(token: String) -> Result<Self, BotError> {
        let client = Client::builder()
            .timeout(std::time::Duration::from_secs(45))
            .build()
            .map_err(|_| BotError)?;
        Ok(Self { token, client })
    }

    async fn call<T: DeserializeOwned>(&self, method: &str, payload: Value) -> Result<T, BotError> {
        let url = format!("https://api.telegram.org/bot{}/{method}", self.token);
        let response = self
            .client
            .post(url)
            .json(&payload)
            .send()
            .await
            .map_err(|_| BotError)?;
        if !response.status().is_success() {
            return Err(BotError);
        }
        let result: TelegramResponse<T> = response.json().await.map_err(|_| BotError)?;
        if !result.ok {
            return Err(BotError);
        }
        result.result.ok_or(BotError)
    }
}

#[async_trait]
impl BotClient for ReqwestBotClient {
    async fn get_me(&self) -> Result<TelegramUser, BotError> {
        self.call("getMe", json!({})).await
    }

    async fn get_updates(
        &self,
        offset: i64,
        timeout_seconds: u64,
    ) -> Result<Vec<TelegramUpdate>, BotError> {
        self.call(
            "getUpdates",
            json!({
                "offset": offset,
                "timeout": timeout_seconds,
                "allowed_updates": ["message", "callback_query"]
            }),
        )
        .await
    }

    async fn send_message(
        &self,
        chat_id: i64,
        text: &str,
        keyboard: &[Vec<InlineButton>],
    ) -> Result<SentMessage, BotError> {
        let mut payload = json!({"chat_id": chat_id, "text": text});
        if !keyboard.is_empty() {
            payload["reply_markup"] = json!({"inline_keyboard": keyboard});
        }
        let message: TelegramMessage = self.call("sendMessage", payload).await?;
        Ok(SentMessage {
            chat_id: message.chat.id,
            message_id: message.message_id,
        })
    }

    async fn edit_message_text(
        &self,
        chat_id: i64,
        message_id: i64,
        text: &str,
    ) -> Result<(), BotError> {
        let _: Value = self
            .call(
                "editMessageText",
                json!({"chat_id": chat_id, "message_id": message_id, "text": text}),
            )
            .await?;
        Ok(())
    }

    async fn answer_callback_query(
        &self,
        callback_query_id: &str,
        text: Option<&str>,
    ) -> Result<(), BotError> {
        let _: bool = self
            .call(
                "answerCallbackQuery",
                json!({"callback_query_id": callback_query_id, "text": text}),
            )
            .await?;
        Ok(())
    }
}

#[derive(Deserialize)]
struct TelegramResponse<T> {
    ok: bool,
    result: Option<T>,
}
