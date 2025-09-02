#!/bin/bash

# Read .env file and update JSON configuration files
if [ ! -f ".env" ]; then
    echo "Error: .env file not found"
    exit 1
fi

# Source the .env file
source .env

# Function to update JSON files
update_json_file() {
    local file=$1
    local connection_string="Host=${POSTGRES_HOST:-postgres};Port=${POSTGRES_PORT:-5432};Database=${POSTGRES_DB:-mare};Username=${POSTGRES_USER:-mare};Password=${POSTGRES_PASSWORD:-mare}"
    
    if [ -f "$file" ]; then
        # Use sed to replace the connection string
        sed -i "s|\"DefaultConnection\": \"[^\"]*\"|\"DefaultConnection\": \"$connection_string\"|g" "$file"
        echo "Updated $file"
    else
        echo "Warning: $file not found"
    fi
}

# Update all configuration files
update_json_file "config/standalone/server-standalone.json"
update_json_file "config/standalone/services-standalone.json"
update_json_file "config/standalone/files-standalone.json"
update_json_file "config/standalone/authservice-standalone.json"

echo "Configuration update completed"