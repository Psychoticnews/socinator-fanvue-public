using DominatorHouseCore.Enums;
using DominatorHouseCore.Interfaces;
using DominatorUIUtility.IoC;
using FanvueDominatorCore.DbMigrations;
using FanvueDominatorUI.Factories;
using Unity;
using Unity.Extension;

namespace FanvueDominatorUI.IoC
{
    /// <summary>
    /// Unity container extension for Fanvue module.
    /// Registers all Fanvue-specific services with the dependency injection container.
    /// </summary>
    public class FvContainerExtension : UnityContainerExtension
    {
        private const SocialNetworks CurrentNetwork = SocialNetworks.Fanvue;

        protected override void Initialize()
        {
            // Register database migrations
            Container.AddNewExtension<FvDbMigrationUnityExtension>();

            // Register the social network module
            Container.RegisterType<ISocialNetworkModule, FvDominatorModule>(CurrentNetwork.ToString());

            // Register collection factories
            Container.RegisterType<INetworkCollectionFactory, FanvueNetworkCollectionFactory>(CurrentNetwork.ToString());
            Container.RegisterType<IPublisherCollectionFactory, FanvuePublisherCollectionFactory>(CurrentNetwork.ToString());

            // TODO: Register additional Fanvue-specific services as needed
            // Example services to add:
            // Container.RegisterSingleton<IAccountDatabaseConnection, FvAccountDbConnection>(CurrentNetwork.ToString());
            // Container.RegisterType<IJobProcessFactory, FvJobProcessFactory>(CurrentNetwork.ToString());
        }
    }
}
