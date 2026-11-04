using System.Linq;
using System.Threading.Tasks;
using CounterStrikeSharp.API.Modules.Menu;
using Xunit;
using static TestUtils;

namespace NativeTestsPlugin;

public class MenuTests
{
    private static int CenterHtmlTickListeners() =>
        NativeTestsPlugin.Instance.Listeners.Keys.Count(d => d.Target is CenterHtmlMenuInstance);

    // A CenterHtml menu replaced by another one, or closed through MenuManager.CloseActiveMenu
    // (which only resets it), used to keep its OnTick listener for the plugin's lifetime.
    [Fact]
    public async Task CenterHtmlMenu_ReplacedOrClosedMenusDropTheirTickListener()
    {
        var bot = await EnsureAliveBot();
        var baseline = CenterHtmlTickListeners();

        try
        {
            for (var i = 0; i < 10; i++)
            {
                var menu = new CenterHtmlMenu($"Leak test {i}", NativeTestsPlugin.Instance);
                menu.AddMenuOption("Option", (_, _) => { });
                MenuManager.OpenCenterHtmlMenu(NativeTestsPlugin.Instance, bot, menu);
            }

            await WaitFrames(3);
            Assert.Equal(baseline + 1, CenterHtmlTickListeners());

            MenuManager.CloseActiveMenu(bot);
            await WaitFrames(3);
            Assert.Equal(baseline, CenterHtmlTickListeners());
        }
        finally
        {
            MenuManager.CloseActiveMenu(bot);
        }
    }
}
