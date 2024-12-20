# Create the order body with explicit array syntax
$body = @{
    symbol = "NQ DEC24"
    target = @("SHORT", "0")  # Make both elements strings for consistent parsing
    bars = @(@{})  # Empty array with placeholder object
    indicators = @(@{})  # Empty array with placeholder object
} | ConvertTo-Json -Depth 10  # Increase depth to ensure nested arrays are serialized

Write-Host "Sending request with body:`n$body"

$headers = @{
    "Content-Type" = "application/json"
}

Invoke-RestMethod `
    -Method Post `
    -Uri "http://127.0.0.1:8003/set_target" `
    -Body $body `
    -Headers $headers `
    -Verbose  # Add verbose output for debugging
