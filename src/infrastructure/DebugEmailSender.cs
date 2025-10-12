sealed class DebugEmailSender : IEmailSender
{
    public System.Threading.Tasks.Task SendAsync(string to, string subject, string htmlBody)
    {
        Console.WriteLine("=== DEBUG EMAIL ===");
        Console.WriteLine($"To: {to}");
        Console.WriteLine($"Subject: {subject}");
        Console.WriteLine(htmlBody);
        Console.WriteLine("===================");
        return System.Threading.Tasks.Task.CompletedTask;
    }
}
