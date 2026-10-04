using System;
using ScriptDock.I18n;
using Xunit;
using static ScriptDock.Views.ObjC;

namespace ScriptDock.Tests.I18n;

[Collection(AppKitLanguages.CollectionName)]
public sealed class LanguageBootstrapTests
{
    [MacOnlyFact]
    public void AlignAppKit_points_this_process_AppleLanguages_at_the_interface_language()
    {
        var defaults = Send(Class("NSUserDefaults"), "standardUserDefaults");
        var argumentDomain = NSString("NSArgumentDomain");
        // The argument domain is set once per process, so set aside whatever this one already holds and
        // put it back afterwards; the reader's own settings are never touched.
        var before = Send(defaults, "volatileDomainForName:", argumentDomain);
        if (before != IntPtr.Zero)
        {
            Send(before, "retain");
            Send(defaults, "removeVolatileDomainForName:", argumentDomain);
        }

        try
        {
            LanguageBootstrap.AlignAppKit("de");

            var languages = Send(defaults, "objectForKey:", NSString("AppleLanguages"));
            Assert.Equal(1UL, SendForUInt(languages, "count"));
            Assert.Equal("de", String(SendWithIndex(languages, "objectAtIndex:", 0)));
        }
        finally
        {
            Send(defaults, "removeVolatileDomainForName:", argumentDomain);
            if (before != IntPtr.Zero)
            {
                Send(defaults, "setVolatileDomain:forName:", before, argumentDomain);
                Send(before, "release");
            }
        }
    }
}
