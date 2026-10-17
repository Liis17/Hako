# Подпись Windows-сборок

`.github/workflows/release.yml` читает два **Repository secret** в GitHub Actions и передаёт
их в одноимённые переменные среды шага сборки:

- `WINDOWS_SIGNING_PFX_BASE64` — PFX с закрытым ключом, закодированный в Base64 одной строкой.
- `WINDOWS_SIGNING_PFX_PASSWORD` — пароль экспорта PFX; пустой пароль поддерживается workflow.

Настройки: [Settings → Secrets and variables → Actions](https://github.com/Liis17/Hako/settings/secrets/actions).
Используй вкладку **Secrets**. Base64 не шифрует закрытый ключ.
Для проверки подписи CI временно импортирует публичный `Hako.cer` в `LocalMachine\TrustedPeople`
одноразового Windows-раннера. Для самоподписанного сертификата CI также добавляет доверие в `LocalMachine\Root`,
которое требуется проверке Authenticode через `signtool verify /pa` и `Get-AuthenticodeSignature`.
Обе записи доверия и закрытый ключ удаляются после сборки, в том числе при ошибке.
Это настройка одноразового CI-раннера; пользователям для установки нужен только `TrustedPeople`.

## Создание на macOS

Нужны OpenSSL с поддержкой `-addext` и авторизованный GitHub CLI (`gh auth login`).
Эти команды создают самоподписанный сертификат для подписи кода на три года.
`CN=daske` должен точно совпадать с `Identity.Publisher` в `windows/Hako/Package.appxmanifest`.
Если сертификат уже есть, используй его для следующих сборок; не создавай новый на каждый запуск CI.

```sh
signing_dir="$(mktemp -d "$HOME/hako-signing.XXXXXX")"
chmod 700 "$signing_dir"
umask 077

openssl req -x509 -newkey rsa:3072 -sha256 -days 1095 \
  -keyout "$signing_dir/key.pem" -out "$signing_dir/certificate.pem" \
  -subj '/CN=daske' \
  -addext 'basicConstraints=critical,CA:FALSE' \
  -addext 'keyUsage=critical,digitalSignature' \
  -addext 'extendedKeyUsage=codeSigning'

openssl pkcs12 -export \
  -inkey "$signing_dir/key.pem" -in "$signing_dir/certificate.pem" \
  -out "$signing_dir/Hako.pfx" -name 'Hako Windows signing'

base64 < "$signing_dir/Hako.pfx" | tr -d '\n' | \
  gh secret set WINDOWS_SIGNING_PFX_BASE64 --repo Liis17/Hako
gh secret set WINDOWS_SIGNING_PFX_PASSWORD --repo Liis17/Hako

gh secret list --repo Liis17/Hako
```

`openssl req` запрашивает пароль PEM-ключа. `openssl pkcs12` сначала запрашивает его,
а затем **Export Password** для PFX. В последнем `gh secret set` введи именно Export Password.
Значения секретов не нужно выводить в терминал или передавать аргументами командной строки.
Сохрани PFX и пароль в защищённой резервной копии вне репозитория.

Если PFX уже создан, достаточно последних трёх команд, подставив его путь.
Secrets доступны следующему запуску workflow, включая повторный запуск упавшей Windows-задачи.

## Установка пользователем

Самоподписанный сертификат не доверен Windows автоматически. Релиз содержит публичный `Hako.cer`.
После проверки происхождения файла пользователь импортирует его в PowerShell от имени администратора:

```powershell
Import-Certificate -FilePath .\Hako.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople
```

После этого можно открыть MSIX для своей архитектуры. PFX и его пароль пользователям не нужны.

Документация: [создание сертификата для MSIX](https://learn.microsoft.com/en-us/windows/msix/package/create-certificate-package-signing),
[загрузка GitHub Secrets через CLI](https://cli.github.com/manual/gh_secret_set).
