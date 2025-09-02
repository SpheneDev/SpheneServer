@echo off
powershell -ExecutionPolicy Bypass -File update-config.ps1
docker-compose -f compose\mare-standalone.yml --env-file .env -p standalone up -d