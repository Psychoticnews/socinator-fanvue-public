using DominatorHouseCore.Enums;
using DominatorHouseCore.Interfaces;
using DominatorHouseCore.Models;
using DominatorUIUtility.IoC;
using Unity;
using Unity.Resolution;

namespace FanvueDominatorUI.IoC
{
    /// <summary>
    /// Fanvue social network module implementation.
    /// Provides factory methods for network and publisher collections.
    /// </summary>
    public class FvDominatorModule : ISocialNetworkModule
    {
        private readonly IUnityContainer _unityContainer;

        public FvDominatorModule(IUnityContainer unityContainer)
        {
            _unityContainer = unityContainer;
        }

        public SocialNetworks Network => SocialNetworks.Fanvue;

        public INetworkCollectionFactory GetNetworkCollectionFactory(AccessorStrategies strategies)
        {
            return _unityContainer.Resolve<INetworkCollectionFactory>(Network.ToString(),
                new ParameterOverride("strategies", strategies));
        }

        public IPublisherCollectionFactory GetPublisherCollectionFactory()
        {
            return _unityContainer.Resolve<IPublisherCollectionFactory>(Network.ToString());
        }
    }
}
