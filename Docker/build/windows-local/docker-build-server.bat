@echo off
cd ..\..\..\..\
docker build -t sphene-server:latest . -f server\Docker\build\Dockerfile-SpheneServer --no-cache --pull --force-rm
cd server\Docker\build\windows-local