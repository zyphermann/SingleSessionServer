interface IEmailSender
{
    System.Threading.Tasks.Task SendAsync(string to, string subject, string htmlBody);
}
