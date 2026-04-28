using DominatorHouseCore.Interfaces;
using DominatorHouseCore.Models;
using FanvueDominatorUI.FvCoreLibrary;

namespace FanvueDominatorUI.Factories
{
    /// <summary>
    /// Factory for creating Fanvue network collection components.
    /// </summary>
    internal class FanvueNetworkCollectionFactory : INetworkCollectionFactory
    {
        private readonly AccessorStrategies _strategies;

        public FanvueNetworkCollectionFactory(AccessorStrategies strategies)
        {
            _strategies = strategies;
        }

        public INetworkCoreFactory GetNetworkCoreFactory()
        {
            var fvNetworkCoreFactory = new FvNetworkCoreFactory();
            var fvCoreBuilder = FvCoreBuilder.Instance(fvNetworkCoreFactory, _strategies);
            return fvCoreBuilder.GetFvCoreObjects();
        }
    }
}
