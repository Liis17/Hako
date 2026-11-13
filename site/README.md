# Сайт Hako

Статический лендинг для `hako.liis17.com` без зависимостей и сборщиков.

- `src/` — исходники: `index.html`, `styles.css`, `script.js`, `assets/`.
- `build.sh` — собирает `dist/`: копирует файлы и добавляет хеш содержимого к именам CSS и JS.
- `nginx/hako.liis17.com.conf` — конфиг nginx: редирект на HTTPS, сертификаты certbot, gzip, кеширование и CSP.

## Сборка и выкладка

```sh
./site/build.sh
rsync -a --delete site/dist/ user@server:/var/www/hako.liis17.com/
```

На сервере:

```sh
sudo cp hako.liis17.com.conf /etc/nginx/sites-available/
sudo ln -s /etc/nginx/sites-available/hako.liis17.com.conf /etc/nginx/sites-enabled/
sudo certbot certonly --webroot -w /var/www/hako.liis17.com -d hako.liis17.com
sudo nginx -t && sudo systemctl reload nginx
```

Если сертификата ещё нет, `nginx -t` не пройдёт: на время получения оставьте только блок `listen 80` или получите сертификат через `certbot certonly --standalone`.

## Динамические данные

`script.js` в браузере запрашивает:

- `api.github.com/repos/Liis17/Hako/releases` — последние стабильные `macos-vX.Y.Z` и `windows-vX.Y.Z` и прямые ссылки на `Hako-X.Y.Z-macos.dmg`, `Hako-X.Y.Z-windows-{x64,arm64}.msix`, `Hako.cer`;
- `piston-meta.mojang.com` — последнюю релизную версию Minecraft для иллюстраций.

Без сети или при лимите GitHub API кнопки ведут на страницу GitHub Releases, а версии остаются из HTML. Новые внешние адреса нужно добавить в `connect-src` CSP в конфиге nginx.
