$file = "m:\Projects\Valerie\Program.cs"
$content = Get-Content $file -Raw

$startTag = "while (true)"
$endTag = "Console.WriteLine(""Valerie text core shut down."");"

$startIndex = $content.IndexOf($startTag)
$endIndex = $content.IndexOf($endTag)

if ($startIndex -ge 0 -and $endIndex -gt $startIndex) {
    $beforeLoop = $content.Substring(0, $startIndex)
    $afterLoop = $content.Substring($endIndex + $endTag.Length + 10) # rough skip of braces
    
    # Extract the loop body
    $loopCode = $content.Substring($startIndex, $endIndex - $startIndex)
    
    # We replace the loop body with the WinForms startup
    $newMainEnd = "
            // Start WinForms Application Context
            Application.Run(new ValerieApplicationContext(HandleUserTurnAsync));
        }

        public static async Task HandleUserTurnAsync(string input)
        {
            if (string.IsNullOrEmpty(input)) return;
"
    
    # We need to adapt the loopCode to remove while(true) and Console.ReadLine()
    # It's easier to just use Regex
    $loopBody = $loopCode -replace "(?s)while \(true\)\s*\{", ""
    $loopBody = $loopBody -replace "(?s)string\? rawInput = Console\.ReadLine\(\);.*?string input = rawInput\.Trim\(\);", "string rawInput = input;"
    # remove the trailing brace of the while loop
    $loopBody = $loopBody.Substring(0, $loopBody.LastIndexOf("}"))
    
    $newContent = $beforeLoop + $newMainEnd + $loopBody + "`n        }" + "`n        " + $endTag + $afterLoop
    
    Set-Content $file $newContent
    Write-Host "Refactored Program.cs successfully."
} else {
    Write-Host "Failed to find boundaries."
}
