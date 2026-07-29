param(
    [switch]$ReplaceExisting
)

$ErrorActionPreference = "Stop"

function ConvertFrom-SecureValue {
    param([Security.SecureString]$Value)

    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($Value)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    }
    finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}

function Configure-Key {
    param(
        [string]$Name,
        [string]$Provider
    )

    $userValue = [Environment]::GetEnvironmentVariable($Name, "User")
    if (-not [string]::IsNullOrWhiteSpace($userValue) -and -not $ReplaceExisting) {
        Write-Host "$Provider is already configured for this Windows user."
        return
    }

    $processValue = [Environment]::GetEnvironmentVariable($Name, "Process")
    if ([string]::IsNullOrWhiteSpace($userValue) -and
        -not [string]::IsNullOrWhiteSpace($processValue)) {
        $answer = Read-Host "Persist the currently loaded $Provider key for future restarts? [Y/n]"
        if ([string]::IsNullOrWhiteSpace($answer) -or
            $answer.Trim().StartsWith("y", [StringComparison]::OrdinalIgnoreCase)) {
            [Environment]::SetEnvironmentVariable($Name, $processValue, "User")
            Write-Host "$Provider key persisted without printing it."
            return
        }
    }

    $secureValue = Read-Host "Enter $Provider key, or press Enter to skip" -AsSecureString
    if ($secureValue.Length -eq 0) {
        Write-Host "$Provider skipped."
        return
    }

    $plainValue = ConvertFrom-SecureValue $secureValue
    try {
        [Environment]::SetEnvironmentVariable($Name, $plainValue.Trim(), "User")
    }
    finally {
        $plainValue = $null
    }
    Write-Host "$Provider key configured for this Windows user."
}

Configure-Key -Name "GROQ_API_KEY" -Provider "Groq"
Configure-Key -Name "GEMINI_API_KEY" -Provider "Gemini"

Write-Host ""
Write-Host "Restart Play Mode so LLMBrainService rebuilds its provider pool."
Write-Host "Ollama or another local model service is not required."
