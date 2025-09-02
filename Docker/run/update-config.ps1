#!/usr/bin/env pwsh
# Script to update JSON configuration files with values from .env file
# This allows centralized configuration management through the .env file

Param(
    [string]$EnvFile = ".env",
    [string]$ConfigDir = "config/standalone"
)

# Function to read .env file and return hashtable
function Read-EnvFile {
    param([string]$FilePath)
    
    $envVars = @{}
    
    if (Test-Path $FilePath) {
        Get-Content $FilePath | ForEach-Object {
            $line = $_.Trim()
            # Skip comments and empty lines
            if ($line -and !$line.StartsWith('#')) {
                $parts = $line.Split('=', 2)
                if ($parts.Length -eq 2) {
                    $key = $parts[0].Trim()
                    $value = $parts[1].Trim()
                    $envVars[$key] = $value
                }
            }
        }
    }
    
    return $envVars
}

# Function to update connection string in JSON file
function Update-ConnectionString {
    param(
        [string]$JsonFile,
        [hashtable]$EnvVars
    )
    
    if (Test-Path $JsonFile) {
        Write-Host "Updating $JsonFile..."
        
        # Read JSON content
        $jsonContent = Get-Content $JsonFile -Raw | ConvertFrom-Json
        
        # Build connection string with values from .env
        $dbHost = if ($EnvVars.ContainsKey('POSTGRES_HOST')) { $EnvVars['POSTGRES_HOST'] } else { 'localhost' }
        $dbPort = if ($EnvVars.ContainsKey('POSTGRES_PORT')) { $EnvVars['POSTGRES_PORT'] } else { '5432' }
        $dbDatabase = if ($EnvVars.ContainsKey('POSTGRES_DB')) { $EnvVars['POSTGRES_DB'] } else { 'mare' }
        $dbUsername = if ($EnvVars.ContainsKey('POSTGRES_USER')) { $EnvVars['POSTGRES_USER'] } else { 'mare' }
        $dbPassword = if ($EnvVars.ContainsKey('POSTGRES_PASSWORD')) { $EnvVars['POSTGRES_PASSWORD'] } else { 'secretdevpassword' }
        
        # For Docker containers, use Unix socket
        $connectionString = "Host=/var/run/postgresql;Port=$dbPort;Database=$dbDatabase;Username=$dbUsername;Password=$dbPassword;Keepalive=15;Minimum Pool Size=10;Maximum Pool Size=50;No Reset On Close=true;Max Auto Prepare=50;Enlist=false"
        
        # Update connection string
        $jsonContent.ConnectionStrings.DefaultConnection = $connectionString
        
        # Write back to file
        $jsonContent | ConvertTo-Json -Depth 10 | Set-Content $JsonFile -Encoding UTF8
        
        Write-Host "Updated $JsonFile successfully."
    } else {
        Write-Warning "File $JsonFile not found."
    }
}

# Main execution
Write-Host "Reading environment variables from $EnvFile..."
$envVars = Read-EnvFile $EnvFile

if ($envVars.Count -eq 0) {
    Write-Error "No environment variables found in $EnvFile"
    exit 1
}

Write-Host "Found $($envVars.Count) environment variables."

# Update all JSON configuration files
$configFiles = @(
    "$ConfigDir/server-standalone.json",
    "$ConfigDir/services-standalone.json",
    "$ConfigDir/files-standalone.json",
    "$ConfigDir/authservice-standalone.json"
)

foreach ($configFile in $configFiles) {
    Update-ConnectionString -JsonFile $configFile -EnvVars $envVars
}

Write-Host "Configuration update completed!"
Write-Host "All JSON files now use the database credentials from $EnvFile"