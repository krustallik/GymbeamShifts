# CI/CD і розгортання двох ботів

## Цільова схема

Один Docker-образ запускається у двох ізольованих контейнерах:

```text
https://bot1.mapa-svietidiel.sk -> gymbeam-bot-1:8080
https://bot2.mapa-svietidiel.sk -> gymbeam-bot-2:8080
```

Кожен бот має власні `.env`, `appconfig.json`, лог, GymBeam-акаунт,
Telegram-параметри й адміністратора. Caddy автоматично отримує та поновлює
HTTPS-сертифікати. Додаткова Basic Auth перед адмінкою не використовується.
Кожен bot-контейнер має ліміт оперативної пам'яті 768 MiB і сумарний
ліміт RAM + swap 1536 MiB.

## DNS

У Websupport уже налаштовано wildcard A-запис:

```text
*.mapa-svietidiel.sk -> 84.247.182.209
```

Тому окремі записи для `bot1` і `bot2` не обов'язкові. Перед запуском перевір:

```bash
dig +short bot1.mapa-svietidiel.sk
dig +short bot2.mapa-svietidiel.sk
```

Обидві команди мають повернути `84.247.182.209`. Не змінюй наявні записи
`admin`, `mail`, `webmail`, `smtp`, `pop3`, `imap`.

## Вимоги до сервера

- Ubuntu з Docker Engine, Docker Compose v2, `curl` і `python3`
- відкриті TCP-порти 22, 80, 443
- бажано щонайменше 4 GB RAM для двох Chromium-процесів
- репозиторій уже клонований у каталог із `docker-compose.yml`

```bash
sudo ufw allow 22/tcp
sudo ufw allow 80/tcp
sudo ufw allow 443/tcp
sudo ufw status
```

Не відкривай порт 8080 назовні.

## Підготовка сервера перед першим push нової схеми

Цей етап виконай до push у `main`, щоб перший автоматичний deploy уже знайшов
обидва набори конфігурації.

```bash
cd /шлях/із/секрету/DEPLOY_PATH
mkdir -p instances/bot1/runtime-data instances/bot2/runtime-data backups
```

Збережи резервну копію поточного одиночного бота:

```bash
BACKUP_DIR="$HOME/gymbeam-before-two-bots-$(date -u +%Y%m%dT%H%M%SZ)"
mkdir -m 700 "$BACKUP_DIR"
cp GymBeamShiftsControllerX/.env "$BACKUP_DIR/bot1.env"
cp GymBeamShiftsControllerX/appconfig.json "$BACKUP_DIR/bot1.appconfig.json"
cp -a runtime-data "$BACKUP_DIR/runtime-data"
chmod 600 "$BACKUP_DIR/bot1.env" "$BACKUP_DIR/bot1.appconfig.json"
```

Перенеси чинні налаштування у bot1:

```bash
cp GymBeamShiftsControllerX/.env instances/bot1/.env
cp GymBeamShiftsControllerX/appconfig.json instances/bot1/appconfig.json
```

Підготуй bot2 на основі тих самих файлів:

```bash
cp GymBeamShiftsControllerX/.env instances/bot2/.env
cp GymBeamShiftsControllerX/appconfig.json instances/bot2/appconfig.json
nano instances/bot2/.env
```

У `instances/bot2/.env` заміни всі значення другого бота. Обов'язкові поля:

```dotenv
GYMBEAM_AUTH_LOGIN=
GYMBEAM_AUTH_PASSWORD=
GYMBEAM_TELEGRAM_BOT_TOKEN=
GYMBEAM_TELEGRAM_CHAT_ID=
GYMBEAM_ADMIN_USER=
GYMBEAM_ADMIN_PASSWORD=
GYMBEAM_ADMIN_TOKEN_SECRET=
GYMBEAM_ADMIN_PORT=8080
GYMBEAM_ADMIN_HOST=*
```

Створи новий секрет сесії для bot2:

```bash
openssl rand -hex 32
```

Bot1 і bot2 повинні мати різні `GYMBEAM_ADMIN_TOKEN_SECRET`. За потреби також
онови секрет bot1 новим випадковим значенням.

Захисти файли:

```bash
chmod 600 instances/bot1/.env instances/bot1/appconfig.json
chmod 600 instances/bot2/.env instances/bot2/appconfig.json
```

Якщо чинний `appconfig.json` змінювався через веб і Git показує його як modified,
після копіювання в `instances/bot1` поверни лише tracked-копію:

```bash
git restore GymBeamShiftsControllerX/appconfig.json
```

Реальний bot1-конфіг уже збережений у `instances/bot1/appconfig.json`.

## GitHub Actions

Workflow `.github/workflows/ci.yml` виконує:

1. restore, build і всі .NET-тести;
2. підготовку безпечних тимчасових deployment-файлів;
3. `docker compose config --quiet`;
4. збірку Docker-образу;
5. валідацію Caddyfile;
6. SSH deploy після push у `main`.

У GitHub Environment `production` потрібні секрети:

| Secret | Значення |
|---|---|
| `SSH_HOST` | `84.247.182.209` |
| `SSH_USER` | користувач Ubuntu для deploy |
| `SSH_PRIVATE_KEY` | приватний SSH-ключ GitHub Actions |
| `DEPLOY_PATH` | каталог репозиторію на сервері |

Секрети ботів у GitHub додавати не потрібно: вони залишаються тільки на сервері.

## Що робить deploy-скрипт

`scripts/deploy.sh`:

1. отримує `main` через fast-forward pull;
2. перевіряє Docker Compose, Caddy та безпечність runtime-каталогів;
3. створює timestamped snapshot конфігурації, маршрутів, образів і `docker inspect` усіх managed-ботів;
4. перевіряє Compose;
5. один раз збирає спільний образ;
6. знаходить усі bot-контейнери за labels `com.gymbeam.managed=true` і `com.gymbeam.role=bot`;
7. пересоздає кожен знайдений контейнер зі свіжим `gymbeam-shifts-bot:latest`, зберігаючи labels, environment, bind mounts, network, healthcheck, restart policy та попередній running/stopped стан, а ліміт RAM нормалізує до 768 MiB;
8. для кожного запущеного бота чекає стану `healthy` і перевіряє `/healthz` безпосередньо всередині контейнера;
9. запускає Caddy;
10. лише після повного успіху видаляє тимчасові backup-контейнери; при помилці повертає попередні контейнери та image.

Імена ботів не зашиті в deploy-скрипт. Це однаково працює для Compose-сервісів і ботів, створених через Admin Manager, наприклад `bot-ihor` або `bot-andriana`.

## Оновлення ліміту RAM на вже створених ботах

Після push у `main` GitHub Actions запускає `scripts/deploy.sh`. Deploy знаходить
усі контейнери з labels `com.gymbeam.managed=true` і `com.gymbeam.role=bot`,
створює безпечний snapshot, а потім пересоздає їх із лімітом 768 MiB RAM і
1536 MiB RAM + swap. Це
стосується як `gymbeam-bot-1` і `gymbeam-bot-2`, так і ботів, створених через
Admin Manager. Простого `docker restart` недостатньо, оскільки він не змінює
HostConfig контейнера.

Після успішного deploy перевір ліміти на сервері:

```bash
docker ps -aq \
  --filter label=com.gymbeam.managed=true \
  --filter label=com.gymbeam.role=bot \
  | xargs -r docker inspect \
      --format '{{.Name}} RAM={{.HostConfig.Memory}} RAM+SWAP={{.HostConfig.MemorySwap}}'
```

Для кожного бота очікується `RAM=805306368 RAM+SWAP=1610612736`. Якщо автоматичний workflow не
запускався, виконай той самий безпечний deploy вручну:

```bash
cd /шлях/із/секрету/DEPLOY_PATH
bash scripts/deploy.sh
```

Для термінової зміни одного контейнера без повного deploy можна використати:

```bash
docker update --memory 768m --memory-swap 1536m ІМ'Я_КОНТЕЙНЕРА
```

Наступний штатний deploy однаково закріпить для нього 768 MiB.

## Перший запуск

Після підготовки server-local файлів зроби push у `main` і стеж за GitHub
Actions. Також deploy можна перевірити вручну:

```bash
cd /шлях/із/секрету/DEPLOY_PATH
bash scripts/deploy.sh
```

Перевір контейнери:

```bash
docker compose ps
docker compose logs --tail=100 gymbeam-bot-1
docker compose logs --tail=100 gymbeam-bot-2
docker compose logs --tail=100 caddy
```

Перевір HTTPS:

```bash
curl -fsS https://bot1.mapa-svietidiel.sk/healthz
curl -fsS https://bot2.mapa-svietidiel.sk/healthz
```

Очікувана відповідь:

```json
{"status":"ok"}
```

Після цього відкрий:

- `https://bot1.mapa-svietidiel.sk`
- `https://bot2.mapa-svietidiel.sk`

Перевір, що кожен логін працює лише у своїй адмінці, налаштування різні, а
повідомлення надходять у правильні Telegram-чати.

## Щоденна експлуатація

Логи:

```bash
tail -f instances/bot1/runtime-data/app.log
tail -f instances/bot2/runtime-data/app.log
```

Перезапуск одного бота:

```bash
docker compose restart gymbeam-bot-1
docker compose restart gymbeam-bot-2
```

Стан ресурсів:

```bash
docker stats
free -h
df -h
```

Налаштування, змінені через кожну вебадмінку, записуються у відповідний
`instances/botN/appconfig.json` і не перезаписуються наступним deploy.

## Відновлення конфігурації

Переглянь резервні копії:

```bash
ls -la backups
```

Перед відновленням зупини потрібний контейнер, скопіюй його `.env` і JSON із
обраного backup та запусти контейнер знову.
