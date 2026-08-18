#!/bin/sh
set -eu

while true; do
  printf 'HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 15\r\nConnection: close\r\n\r\n{"status":"ok"}' \
    | nc -l -p 8080
done
