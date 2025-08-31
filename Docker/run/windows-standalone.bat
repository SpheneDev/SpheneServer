@echo off
docker-compose -f compose\mare-standalone.yml --env-file .env -p standalone up -d