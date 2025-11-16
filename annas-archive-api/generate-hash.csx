#r "nuget: BCrypt.Net-Next, 4.0.3"
using BCrypt.Net;

if (Args.Count == 0)
{
    Console.WriteLine("Usage: dotnet script generate-hash.csx <password>");
    return;
}

var password = Args[0];
var hash = BCrypt.Net.BCrypt.HashPassword(password);
Console.WriteLine($"Password: {password}");
Console.WriteLine($"Hash: {hash}");
