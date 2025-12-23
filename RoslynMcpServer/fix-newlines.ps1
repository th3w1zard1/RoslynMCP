$files = Get-ChildItem -Recurse -Filter *.cs
foreach ($file in $files) {
    $content = Get-Content $file.FullName -Raw

    # Fix string literals: convert newlines inside string literals back to \n
    # This regex finds string interpolations and regular strings
    $content = $content -replace '(\$?"[^"]*?)\r?\n([^"]*?")', '$1\n$2'
    $content = $content -replace '(\$?"[^"]*?)\r?\n([^"]*?")', '$1\n$2'  # Run twice for nested cases
    $content = $content -replace '(@"[^"]*?)\r?\n([^"]*?")', '$1\n$2'

    # Also handle cases where newline is at end of string before closing quote
    $content = $content -replace '(\$?"[^"]*?)\r?\n\s*"', '$1\n"'
    $content = $content -replace '(@"[^"]*?)\r?\n\s*"', '$1\n"'

    Set-Content -Path $file.FullName -Value $content -NoNewline
}

