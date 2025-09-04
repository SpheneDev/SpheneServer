@echo off
cd ..\..\..\..\
docker build -t mare-synchronos-authservice:latest . -f server\Docker\build\Dockerfile-MareSynchronosAuthService --no-cache --pull --force-rm
cd server\Docker\build\windows-local
