# CI/CD: повна інструкція (GitHub Actions → Ubuntu)

Репозиторій: `https://github.com/krustallik/GymbeamShifts`

## Що відбувається автоматично

```
Push у main (з твого ПК)
        ↓
GitHub Actions: dotnet test (85 тестів)
        ↓ (якщо ✅)
GitHub Actions: SSH на Ubuntu-сервер
        ↓
scripts/deploy.sh → git pull + docker compose up -d --build
```

**Pull request** → тільки тести, без deploy.  
**Push у main** → тести + deploy.

---

## Де що робиться (швидка таблиця)

| Крок | Де | Скільки разів |
|------|-----|---------------|
| Код, тести, workflow | ПК → git push | постійно |
| GitHub Secrets | GitHub.com (браузер) | 1 раз |
| Environment `production` | GitHub.com (браузер) | 1 раз |
| SSH-ключ для Actions | ПК (генерація) | 1 раз |
| Публічний ключ у `authorized_keys` | **Сервер** | 1 раз |
| `git pull` доступ (deploy key) | **Сервер** + GitHub | 1 раз (якщо repo private) |
| `.env`, htpasswd, runtime-data | **Сервер** | 1 раз (вже є, якщо бот працює) |
| Оновлення до нової версії | **Сервер** | 1 раз зараз, далі автоматично |
| Щоденна робота | ПК: `git push` | кожна зміна |

---

# ЧАСТИНА A — На ПК (Windows)

## A1. Переконайся, що CI/CD файли в git

У репозиторії мають бути:

- `.github/workflows/ci.yml`
- `scripts/deploy.sh`
- `GymBeamShiftsController.sln` + тести

Якщо зміни ще не на GitHub:

```powershell
cd D:\Gymbeam\GymBeamShiftsController
git status
git push origin main
```

## A2. Згенеруй SSH-ключ для GitHub Actions

**На ПК** (PowerShell або Git Bash):

```powershell
ssh-keygen -t ed25519 -C "github-actions-gymbeam" -f gymbeam_deploy_key -N '""'
```

З'являться 2 файли:

- `gymbeam_deploy_key` — **приватний** → піде в GitHub Secret
- `gymbeam_deploy_key.pub` — **публічний** → піде на сервер

⚠️ Приватний ключ **ніколи** не коміть у git і нікому не надсилай.

---

# ЧАСТИНА B — GitHub (браузер)

Відкрий: `https://github.com/krustallik/GymbeamShifts`

## B1. Створи Environment `production`

1. **Settings → Environments → New environment**
2. Name: `production`
3. (Опційно) **Required reviewers** — якщо хочеш підтверджувати deploy вручну
4. **Save protection rules**

> Без цього job `deploy` може падати з помилкою про environment.

## B2. Додай Secrets

**Settings → Secrets and variables → Actions → New repository secret**

| Secret | Що вставити | Приклад |
|--------|-------------|---------|
| `SSH_HOST` | IP або домен сервера | `203.0.113.10` |
| `SSH_USER` | Linux-користувач для SSH | `root` або `deploy` |
| `SSH_PRIVATE_KEY` | **Весь** вміст файлу `gymbeam_deploy_key` | `-----BEGIN OPENSSH PRIVATE KEY-----...` |
| `DEPLOY_PATH` | Шлях до проекту на сервері (де `docker-compose.yml`) | `/home/user/GymbeamShifts` |

`DEPLOY_PATH` дізнаєшся на сервері командою `pwd` у папці проекту.

---

# ЧАСТИНА C — На Ubuntu-сервері

Підключись:

```bash
ssh YOUR_USER@YOUR_SERVER_IP
cd /шлях/до/проекту    # тут має бути docker-compose.yml
pwd                     # цей шлях → DEPLOY_PATH у GitHub
```

## C1. Якщо проект УЖЕ запущений (твій випадок)

### Backup (рекомендовано)

```bash
cd /шлях/до/проекту
cp GymBeamShiftsControllerX/.env ~/.env.backup
cp GymBeamShiftsControllerX/appconfig.json ~/appconfig.backup
cp nginx/.htpasswd ~/.htpasswd.backup
```

### Перевір git

```bash
git status
git remote -v
```

Має бути remote `origin` → `github.com/krustallik/GymbeamShifts`.

### Онови код до актуальної версії (вручну, один раз)

```bash
git fetch origin main
git pull origin main
```

Перевір, що в `GymBeamShiftsControllerX/appconfig.json` є:

```json
"FavoriteShiftUsers": []
```

Додай улюблених ведучих (якщо потрібно):

```json
"FavoriteShiftUsers": [
  "Andrea Pavlíková"
]
```

### Перезапусти контейнер

```bash
docker compose up -d --build
docker compose ps
tail -n 30 runtime-data/app.log
```

Перевір адмінку: `http://YOUR_SERVER_IP/`

---

## C2. Дозволь GitHub Actions заходити по SSH

На сервері під тим же user, що в `SSH_USER`:

```bash
mkdir -p ~/.ssh
chmod 700 ~/.ssh
nano ~/.ssh/authorized_keys
```

Встав **одним рядком** вміст `gymbeam_deploy_key.pub` (з ПК), збережи.

```bash
chmod 600 ~/.ssh/authorized_keys
```

### Перевір з ПК

```powershell
ssh -i gymbeam_deploy_key YOUR_USER@YOUR_SERVER_IP "cd /шлях/до/проекту && git status"
```

Якщо заходить без пароля — OK.

---

## C3. Якщо репозиторій PRIVATE — deploy key для git pull

GitHub Actions заходить по SSH, але **`git pull` на сервері** теж потребує доступу до GitHub.

### На сервері

```bash
ssh-keygen -t ed25519 -f ~/.ssh/github_deploy -N ""
cat ~/.ssh/github_deploy.pub
```

### У GitHub

**Settings → Deploy keys → Add deploy key**

- Title: `ubuntu-server`
- Key: вміст `github_deploy.pub`
- ✅ Allow read-only access

### На сервері — налаштуй git

```bash
cd /шлях/до/проекту
git remote set-url origin git@github.com:krustallik/GymbeamShifts.git
git pull origin main
```

Якщо питає fingerprint — підтверди.

> Якщо repo **public**, deploy key не обов'язковий — `git pull` через HTTPS теж працює.

---

## C4. Перевір, що deploy-скрипт працює

```bash
cd /шлях/до/проекту
bash scripts/deploy.sh
```

Очікуваний результат:

- `git pull` без помилок
- `docker compose up -d --build`
- `Deploy finished successfully.`

Якщо помилка `.env not found` — файл має бути тут:

```
/шлях/до/проекту/GymBeamShiftsControllerX/.env
```

Якщо помилка `.htpasswd not found`:

```bash
printf "admin:$(openssl passwd -apr1 'YOUR_PASSWORD')\n" > nginx/.htpasswd
```

---

# ЧАСТИНА D — Перший автоматичний deploy

## D1. Запуск

**Варіант 1** — push з ПК:

```powershell
git commit --allow-empty -m "Trigger CI/CD deploy"
git push origin main
```

**Варіант 2** — вручну в GitHub:

**Actions → CI → Run workflow → Run workflow**

> `Run workflow` запускає тести. Deploy автоматично піде тільки якщо це push у `main` (не workflow_dispatch для deploy job — deploy прив'язаний до push).  
> Тому для першого deploy краще зробити push.

## D2. Перевір в GitHub

**Actions → останній workflow run**

Має бути:

1. ✅ **test** (~85 tests passed)
2. ✅ **Deploy to Ubuntu**

## D3. Перевір на сервері

```bash
cd /шлях/до/проекту
git log -1 --oneline
docker compose ps
tail -n 20 runtime-data/app.log
```

---

# Щоденна робота (після налаштування)

Тільки на **ПК**:

```powershell
# змінив код
git add .
git commit -m "опис змін"
git push origin main
```

GitHub сам:

1. прогонить тести
2. задеплоїть на сервер

**На сервер заходити не потрібно**, якщо все налаштовано.

---

# Що НЕ їде через CI/CD (залишається на сервері)

| Файл / папка | Чому |
|--------------|------|
| `GymBeamShiftsControllerX/.env` | секрети, в `.gitignore` |
| `nginx/.htpasswd` | пароль Nginx, в `.gitignore` |
| `runtime-data/` | логи, в `.gitignore` |

`appconfig.json` **оновлюється з git** при deploy. Якщо редагуєш його тільки на сервері — зміни можуть перезаписатись. Краще міняти через admin UI або тримати в git.

---

# Troubleshooting

| Проблема | Де дивитись | Рішення |
|----------|-------------|---------|
| `Permission denied (publickey)` | GitHub Actions log | перевір `SSH_PRIVATE_KEY`, `authorized_keys`, `SSH_USER` |
| `environment production not found` | GitHub Actions | створи Environment `production` (B1) |
| `GymBeamShiftsControllerX/.env not found` | сервер | створи `.env` на сервері |
| `git pull` failed / auth | сервер | deploy key (C3) або public repo |
| `docker: permission denied` | сервер | `sudo usermod -aG docker $USER`, relogin |
| deploy ✅ але бот старий | сервер | `docker compose ps`, `git log -1` |
| тести падають | GitHub Actions | виправ код, deploy не піде поки test ❌ |

---

# Чеклист «CI/CD повністю працює»

- [ ] Код з `.github/workflows/ci.yml` і `scripts/deploy.sh` у `main` на GitHub
- [ ] Environment `production` створений у GitHub
- [ ] 4 Secrets: `SSH_HOST`, `SSH_USER`, `SSH_PRIVATE_KEY`, `DEPLOY_PATH`
- [ ] `gymbeam_deploy_key.pub` у `~/.ssh/authorized_keys` на сервері
- [ ] SSH з ПК працює: `ssh -i gymbeam_deploy_key USER@HOST`
- [ ] `git pull origin main` працює на сервері
- [ ] `.env` і `nginx/.htpasswd` існують на сервері
- [ ] `bash scripts/deploy.sh` проходить вручну
- [ ] Push у `main` → Actions: test ✅ + Deploy to Ubuntu ✅
- [ ] Бот працює після deploy: `docker compose ps`, admin UI відкривається

Коли всі пункти ✅ — CI/CD працює повністю.
