use std::{
    collections::{HashMap, VecDeque},
    net::IpAddr,
};

#[derive(Clone, Copy, Debug, Eq, Hash, PartialEq)]
pub enum RateAction {
    Register,
    Login,
    Exchange,
}

#[derive(Default)]
pub struct RateLimiter {
    requests: HashMap<(IpAddr, RateAction), VecDeque<i64>>,
}

impl RateLimiter {
    const WINDOW_SECONDS: i64 = 60;

    pub fn allow(&mut self, ip: IpAddr, action: RateAction, now: i64) -> bool {
        let limit = match action {
            RateAction::Register => 5,
            RateAction::Login => 10,
            RateAction::Exchange => 30,
        };
        let key = (ip, action);
        let requests = self.requests.entry(key).or_default();
        while requests
            .front()
            .is_some_and(|oldest| *oldest <= now - Self::WINDOW_SECONDS)
        {
            requests.pop_front();
        }
        if requests.len() >= limit {
            return false;
        }
        requests.push_back(now);
        true
    }
}

#[cfg(test)]
mod tests {
    use std::net::IpAddr;

    use super::{RateAction, RateLimiter};

    #[test]
    fn limits_each_ip_and_action_in_a_sliding_window() {
        let ip: IpAddr = "192.0.2.10".parse().expect("IP address");
        let other_ip: IpAddr = "192.0.2.11".parse().expect("IP address");
        let mut limiter = RateLimiter::default();

        for _ in 0..5 {
            assert!(limiter.allow(ip, RateAction::Register, 100));
        }
        assert!(!limiter.allow(ip, RateAction::Register, 101));
        assert!(limiter.allow(other_ip, RateAction::Register, 101));
        assert!(limiter.allow(ip, RateAction::Login, 101));
        assert!(limiter.allow(ip, RateAction::Register, 160));
    }
}
