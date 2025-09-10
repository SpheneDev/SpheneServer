#!/bin/sh
./update-config.sh
docker compose -f compose/sphene-standalone.yml --env-file .env -p standalone up