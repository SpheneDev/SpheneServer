@echo off
cd ..\..\..\..\
docker build -t mare-synchronos-server:latest . -f server\Docker\build\Dockerfile-MareSynchronosServer --no-cache --pull --force-rm
cd server\Docker\build\windows-local