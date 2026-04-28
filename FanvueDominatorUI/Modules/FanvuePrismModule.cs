using Prism.Ioc;
using Prism.Modularity;

namespace FanvueDominatorUI.Modules
{
    /// <summary>
    /// Prism module for Fanvue.
    /// Handles view registration with the Prism framework.
    /// </summary>
    public class FanvuePrismModule : IModule
    {
        public void RegisterTypes(IContainerRegistry containerRegistry)
        {
            // TODO: Register navigation views as needed
            // Example:
            // containerRegistry.RegisterForNavigation<FanvueMainView>();
        }

        public void OnInitialized(IContainerProvider containerProvider)
        {
            // TODO: Initialize any services that need to run at startup
        }
    }
}
