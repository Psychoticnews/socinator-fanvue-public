using DominatorHouseCore.Enums;
using DominatorHouseCore.Interfaces;
using DominatorHouseCore.Models;
using FanvueDominatorUI.Factories;

namespace FanvueDominatorUI.FvCoreLibrary
{
    /// <summary>
    /// Core builder for Fanvue module.
    /// Builds and configures all Fanvue core components.
    /// </summary>
    public class FvCoreBuilder
    {
        private static FvCoreBuilder _instance;
        private readonly FvNetworkCoreFactory _fvNetworkCoreFactory;
        private readonly AccessorStrategies _strategies;

        // Singleton factory instances
        private static FanvueAccountCountFactory _accountCountFactory;
        private static FanvueAccountUpdateFactory _accountUpdateFactory;

        private FvCoreBuilder(FvNetworkCoreFactory fvNetworkCoreFactory, AccessorStrategies strategies)
        {
            _fvNetworkCoreFactory = fvNetworkCoreFactory;
            _strategies = strategies;
        }

        public static FvCoreBuilder Instance(FvNetworkCoreFactory fvNetworkCoreFactory, AccessorStrategies strategies)
        {
            return _instance ?? (_instance = new FvCoreBuilder(fvNetworkCoreFactory, strategies));
        }

        public INetworkCoreFactory GetFvCoreObjects()
        {
            _fvNetworkCoreFactory.Network = SocialNetworks.Fanvue;
            _fvNetworkCoreFactory.TabHandlerFactory = FvTabHandlerFactory.Instance(_strategies);

            // Account display columns (Followers, Subscribers, Revenue Today, Revenue Week, Total)
            _fvNetworkCoreFactory.AccountCountFactory = GetAccountCountFactory();

            // Account status checking and stats updates
            _fvNetworkCoreFactory.AccountUpdateFactory = GetAccountUpdateFactory();

            // TODO: Add other factories as needed
            // _fvNetworkCoreFactory.AccountUserControlTools = ...
            // _fvNetworkCoreFactory.ReportFactory = ...
            // _fvNetworkCoreFactory.ViewCampaigns = ...

            return _fvNetworkCoreFactory;
        }

        private static FanvueAccountCountFactory GetAccountCountFactory()
        {
            return _accountCountFactory ?? (_accountCountFactory = new FanvueAccountCountFactory());
        }

        private static FanvueAccountUpdateFactory GetAccountUpdateFactory()
        {
            return _accountUpdateFactory ?? (_accountUpdateFactory = new FanvueAccountUpdateFactory());
        }
    }
}
