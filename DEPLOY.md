# Deploy на Ubuntu-сервер через GitHub Actions

Цей flow робить так:

1. Push у `main` → GitHub запускає тести
2. Якщо тести пройшли → GitHub підключається по SSH до твого Ubuntu-сервера
3. На сервері виконується `scripts/deploy.sh`:
   - `git pull` останнього коду
   - `docker compose up -d --build`
4. Секрети **не** їдуть з GitHub — вони лишаються на сервері в `.env`

## Що залишається тільки на сервері (не в git)

- `GymBeamShiftsControllerX/.env` — логін, пароль, Telegram token
- `nginx/.htpasswd` — Basic Auth для Nginx
- `runtime-data/` — логи бота
- локальні зміни в `appconfig.json`, якщо ти їх робив вручну на сервері

---

## 1. Одноразове налаштування Ubuntu-сервера

Підключись до сервера:

```bash
ssh user@YOUR_SERVER_IP
```

### Встанови Docker

```bash
sudo apt update
sudo apt install -y git ca-certificates curl
curl -fsSL https://get.docker.com | sudo sh
sudo usermod -aG docker "$USER"
newgrp docker
docker compose version
```

### Створи deploy-користувача (рекомендовано)

```bash
sudo adduser --disabled-password --gecos "" deploy
sudo usermod -aG docker deploy
sudo mkdir -p /home/deploy/.ssh
sudo chmod 700 /home/deploy/.ssh
```

### Склонуй репозиторій

```bash
sudo mkdir -p /opt/gymbeam
sudo chown deploy:deploy /opt/gymbeam
sudo -u deploy git clone https://github.com/krustallik/GymbeamShifts.git /opt/gymbeam/app
cd /opt/gymbeam/app
```

### Налаштуй секрети на сервері (один раз)

```bash
cp GymBeamShiftsControllerX/.env.example GymBeamShiftsControllerX/.env   # якщо є example
nano GymBeamShiftsControllerX/.env
```

Мінімум у `.env`:

```env
GYMBEAM_AUTH_LOGIN=...
GYMBEAM_AUTH_PASSWORD=...
GYMBEAM_TELEGRAM_BOT_TOKEN=...
GYMBEAM_TELEGRAM_CHAT_ID=...
GYMBEAM_ADMIN_USER=admin
GYMBEAM_ADMIN_PASSWORD=...
GYMBEAM_ADMIN_TOKEN_SECRET=...
```

Basic Auth для Nginx:

```bash
printf "admin:$(openssl passwd -apr1 'YOUR_PASSWORD')\n" > nginx/.htpasswd
mkdir -p runtime-data
```

### Перший ручний запуск

```bash
docker compose up -d --build
docker compose ps
```

Перевір: `http://YOUR_SERVER_IP/`

---

## 2. SSH-ключ для GitHub Actions

На **своєму комп'ютері** (не на сервері):

```bash
ssh-keygen -t ed25519 -C "github-actions-gymbeam" -f gymbeam_deploy_key -N ""
```

Додай **публічний** ключ на сервер:

```bash
ssh deploy@YOUR_SERVER_IP
mkdir -p ~/.ssh
chmod 700 ~/.ssh
nano ~/.ssh/authorized_keys
# встав вміст gymbeam_deploy_key.pub
chmod 600 ~/.ssh/authorized_keys
```

Перевір:

```bash
ssh -i gymbeam_deploy_key deploy@YOUR_SERVER_IP "cd /opt/gymbeam/app && git status"
```

---

## 3. GitHub Secrets

У репозиторії: **Settings → Secrets and variables → Actions → New repository secret**

| Secret | Приклад | Опис |
|--------|---------|------|
| `SSH_HOST` | `203.0.113.10` | IP або домен сервера |
| `SSH_USER` | `deploy` | Linux-користувач для deploy |
| `SSH_PRIVATE_KEY` | вміст `gymbeam_deploy_key` | приватний ключ (весь файл) |
| `DEPLOY_PATH` | `/opt/gymbeam/app` | шлях до репозиторію на сервері |

Опційно: **Settings → Environments → New environment → `production`**  
→ увімкни **Required reviewers**, якщо хочеш ручне підтвердження перед deploy.

---

## 4. Як працює CI/CD

Файл `.github/workflows/ci.yml`:

- **pull request** → тільки `test`
- **push у main** → `test`, потім `deploy` (якщо test пройшов)

Deploy-крок:

```bash
cd $DEPLOY_PATH
bash scripts/deploy.sh
```

---

## 5. Перевірка після налаштування

1. Зроби commit + push у `main`
2. Відкрий **GitHub → Actions**
3. Має бути:
   - job `test` ✅
   - job `deploy` ✅
4. На сервері:

```bash
cd /opt/gymbeam/app
docker compose ps
tail -n 50 runtime-data/app.log
```

---

## Troubleshooting

### `Permission denied (publickey)`

- перевір `SSH_PRIVATE_KEY` у GitHub Secrets
- перевір `authorized_keys` на сервері
- перевір `SSH_USER` і `SSH_HOST`

### `GymBeamShiftsControllerX/.env not found`

- створи `.env` на сервері вручну (див. вище)

### `docker: permission denied`

```bash
sudo usermod -aG docker deploy
# перелогінься
```

### Deploy перезаписав локальний `appconfig.json`

- або тримай конфіг у git
- або на сервері: `git update-index --skip-worktree GymBeamShiftsControllerX/appconfig.json`

### Хочеш deploy не на кожен push, а вручну

У workflow можна додати:

```yaml
on:
  workflow_dispatch:
```

Тоді deploy запускатиметься кнопкою **Run workflow** в GitHub Actions.

---

## Безпека (коротко)

- окремий `deploy` user, не `root`
- окремий SSH-ключ тільки для CI
- `.env` ніколи не комітити
- закрий порт 8080 ззовні (у тебе вже так — тільки Nginx на `:80`)
- за можливості обмеж SSH firewall-ом лише для GitHub Actions IP або через VPN/bastion
