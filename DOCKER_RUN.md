## Docker Run

1. Set real secrets in `GymBeamShiftsControllerX/.env`.
2. Change basic auth credentials:
   - Linux/macOS: `printf "admin:$(openssl passwd -apr1 'YOUR_PASSWORD')\n" > nginx/.htpasswd`
   - Windows PowerShell with OpenSSL installed: `echo "admin:$(openssl passwd -apr1 YOUR_PASSWORD)" > nginx/.htpasswd`
3. Start:
   - `docker compose up -d --build`
4. Open:
   - `http://SERVER_IP/`

Notes:
- Bot container is internal-only.
- Only Nginx is public on port 80.
- Shift rules/API are available through Nginx with Basic Auth.
