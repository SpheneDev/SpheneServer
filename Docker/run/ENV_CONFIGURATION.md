# Mare Synchronos Environment Configuration

This file explains the usage of environment variables in the `.env` file for the Mare Synchronos Docker setup.

## Overview

The `.env` file contains all important configuration values for the Mare Synchronos system. These values are used by the Docker Compose files and application configurations.

## Important Notes

### For local development:
- The default values in the `.env` file are optimized for local development
- `DEV_MARE_CDNURL` should be set to `http://localhost:6200`
- Database and Redis passwords can use the default values

### For production environments:
- **IMPORTANT**: `JWT_SECRET` must be changed! Use a secure, random string with at least 64 characters
- `POSTGRES_PASSWORD` and `REDIS_PASSWORD` should use secure, unique passwords
- `DEV_MARE_CDNURL` should point to the public URL of your server

## Configuration Categories

### Database Configuration
- `POSTGRES_HOST`: PostgreSQL server host (default: localhost)
- `POSTGRES_PORT`: PostgreSQL server port (default: 5432)
- `POSTGRES_DB`: Database name (default: mare)
- `POSTGRES_USER`: Database user (default: mare)
- `POSTGRES_PASSWORD`: Database password (default: secretdevpassword)

### Redis Configuration
- `REDIS_HOST`: Redis server host (default: redis)
- `REDIS_PORT`: Redis server port (default: 6379)
- `REDIS_PASSWORD`: Redis password (default: secretredispassword)

### Server Configuration
- `JWT_SECRET`: JWT token secret (MUST be changed for production!)
- `SHARD_NAME`: Server shard name (default: Main)
- `METRICS_PORT`: Port for metrics (default: 6050)

### User Limits
- `MAX_EXISTING_GROUPS_BY_USER`: Maximum number of groups per user (default: 6)
- `MAX_JOINED_GROUPS_BY_USER`: Maximum number of joined groups (default: 10)
- `MAX_GROUP_USER_COUNT`: Maximum users per group (default: 100)

### File Management
- `CACHE_DIRECTORY`: Directory for cache files (default: /opt/cache)
- `UNUSED_FILE_RETENTION_PERIOD_IN_DAYS`: Retention period for unused files (default: 7)
- `USE_COLD_STORAGE`: Enable cold storage (default: false)

### Optional: Discord Integration
- `DEV_MARE_DISCORDTOKEN`: Discord bot token (optional)
- `DEV_MARE_DISCORDCHANNEL`: Discord channel ID (optional)
- `DEV_MARE_DISCORDROLE`: Discord role ID (optional)

### Optional: External APIs
- `DEV_MARE_XIVAPIKEY`: XIVAPI key for FFXIV integration (optional)

## Usage

1. Copy the `.env` file and adjust the values to your environment
2. For production environments:
   - Change `JWT_SECRET` to a secure value
   - Set secure passwords for `POSTGRES_PASSWORD` and `REDIS_PASSWORD`
   - Configure `DEV_MARE_CDNURL` with your public URL
3. Start the system with `docker-compose up`

## Fallback Behavior

When environment variables are not set, default values are used:
- In Docker Compose files: `${VARIABLE_NAME:-default_value}`
- In appsettings.json: Environment variables are replaced by placeholders
- In MareDbContext.cs: Fallback to environment variables or default values

## Security Notes

- Never commit the `.env` file with production values to a public repository
- Always use secure, unique passwords for production environments
- The `JWT_SECRET` should be at least 64 characters long and cryptographically secure
- Restrict access to the `.env` file to authorized users only

## Troubleshooting

### Problem: "postgres user authentication failed"
- Check if `POSTGRES_USER` and `POSTGRES_PASSWORD` are set correctly
- Ensure that Docker containers load the environment variables correctly

### Problem: Redis connection error
- Check `REDIS_PASSWORD` in the `.env` file
- Ensure that the Redis container starts with the correct password

### Problem: JWT token error
- Check if `JWT_SECRET` is set and at least 64 characters long
- Ensure that all services use the same JWT_SECRET