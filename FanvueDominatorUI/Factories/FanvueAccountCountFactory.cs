using System.Collections.Generic;
using System.Linq;
using DominatorHouseCore.Interfaces;

namespace FanvueDominatorUI.Factories
{
    /// <summary>
    /// Defines the display columns for Fanvue accounts in the account list.
    /// Columns show: Followers, Subscribers, Revenue Today, Revenue Week, Total Revenue
    /// </summary>
    public class FanvueAccountCountFactory : IAccountCountFactory, IColumnSpecificationProvider
    {
        public string HeaderColumn1Value { get; set; } = "Followers";
        public bool HeaderColumn1Visiblity { get; set; } = true;

        public string HeaderColumn2Value { get; set; } = "Subscribers";
        public bool HeaderColumn2Visiblity { get; set; } = true;

        public string HeaderColumn3Value { get; set; } = "Today";
        public bool HeaderColumn3Visiblity { get; set; } = true;

        public string HeaderColumn4Value { get; set; } = "This Week";
        public bool HeaderColumn4Visiblity { get; set; } = true;

        public string HeaderColumn5Value { get; set; } = "Total Revenue";
        public bool HeaderColumn5Visiblity { get; set; } = true;

        /// <summary>
        /// Returns only the visible column headers for display.
        /// </summary>
        public IEnumerable<string> VisibleHeaders
        {
            get
            {
                var headers = new List<KeyValuePair<string, bool>>
                {
                    new KeyValuePair<string, bool>(HeaderColumn1Value, HeaderColumn1Visiblity),
                    new KeyValuePair<string, bool>(HeaderColumn2Value, HeaderColumn2Visiblity),
                    new KeyValuePair<string, bool>(HeaderColumn3Value, HeaderColumn3Visiblity),
                    new KeyValuePair<string, bool>(HeaderColumn4Value, HeaderColumn4Visiblity),
                    new KeyValuePair<string, bool>(HeaderColumn5Value, HeaderColumn5Visiblity)
                };

                return headers.Where(h => h.Value).Select(h => h.Key);
            }
        }
    }
}
