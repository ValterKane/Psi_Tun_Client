using PsiTun.ViewModels;

namespace PsiTun.Tests;

/// <summary>
/// Ответ сообщается ровно один раз: молчание по таймеру закрывает вопрос, а
/// нажатие после молчания ничего не меняет. От этого зависит, добавится правило
/// или нет.
/// </summary>
public class AutoProxyPromptViewModelTests
{
    [Fact]
    public void TimeoutThenCommand_ReportsSilenceOnce()
    {
        var answers = new List<bool?>();
        var vm = new AutoProxyPromptViewModel("x.com", answers.Add);

        vm.Timeout();
        vm.ProxyCommand.Execute(null);

        Assert.Equal([null], answers);
    }

    [Fact]
    public void CommandThenTimeout_ReportsAnswerOnce()
    {
        var answers = new List<bool?>();
        var vm = new AutoProxyPromptViewModel("x.com", answers.Add);

        vm.DenyCommand.Execute(null);
        vm.Timeout();

        Assert.Equal([false], answers);
    }

    [Fact]
    public void DenyReportsFalse_AndProxyReportsTrue()
    {
        var deny = new List<bool?>();
        new AutoProxyPromptViewModel("x.com", deny.Add).DenyCommand.Execute(null);
        Assert.Equal([false], deny);

        var proxy = new List<bool?>();
        new AutoProxyPromptViewModel("x.com", proxy.Add).ProxyCommand.Execute(null);
        Assert.Equal([true], proxy);
    }

    [Fact]
    public void Message_NamesTheHost()
    {
        var vm = new AutoProxyPromptViewModel("blocked.example", _ => { });
        Assert.Contains("blocked.example", vm.Message);
    }
}
