#!/bin/sh
cd ../../../
docker build -t mare-synchronos-staticfilesserver:latest . -f Docker/build/Dockerfile-MareSynchronosStaticFilesServer --no-cache --pull --force-rm
cd Docker/build/linux-local