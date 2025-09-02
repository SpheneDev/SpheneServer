#!/bin/sh
./update-config.sh
docker compose -f compose/mare-standalone.yml --env-file .env -p standalone up