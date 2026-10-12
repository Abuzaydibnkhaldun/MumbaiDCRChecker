using System;
using System.Collections.Generic;

namespace MumbaiDCRChecker.Services
{
    /// <summary>
    /// Contains Mumbai DCPR 2034 regulation data and lookup functions
    /// </summary>
    public static class MumbaiDCRData
    {
        /// <summary>
        /// Calculate setback based on road width (in meters)
        /// Based on Mumbai DCPR 2034 setback requirements
        /// </summary>
        public static double GetSetbackForRoadWidth(double roadWidth)
        {
            // No road on this side - road-based setback = 0
            // Side/rear marginal open space (building-type minimum) handled by enhanced checker
            if (roadWidth <= 0) return 0.0;
            if (roadWidth < 9.0) return 3.0;
            if (roadWidth < 12.0) return 4.5;
            if (roadWidth < 18.0) return 6.0;
            if (roadWidth < 30.0) return 9.0;
            return 12.0; // Roads >= 30m
        }

        /// <summary>
        /// Get ground coverage limit based on project and zone type
        /// Returns percentage value
        /// </summary>
        public static double GetGroundCoverageLimit(string projectType, string zoneType)
        {
            var key = $"{projectType}_{zoneType}";
            var limits = new Dictionary<string, double>
            {
                { "Residential_City", 50.0 },
                { "Residential_Suburb", 50.0 },
                { "Commercial_City", 60.0 },
                { "Commercial_Suburb", 60.0 },
                { "Mixed_City", 55.0 },
                { "Mixed_Suburb", 55.0 },
                { "Industrial_City", 65.0 },
                { "Industrial_Suburb", 65.0 }
            };

            return limits.ContainsKey(key) ? limits[key] : 50.0;
        }

        /// <summary>
        /// Get maximum building height based on project and zone type
        /// Returns height in meters
        /// </summary>
        public static double GetMaxBuildingHeight(string projectType, string zoneType)
        {
            var key = $"{projectType}_{zoneType}";
            var heights = new Dictionary<string, double>
            {
                { "Residential_City", 70.0 },
                { "Residential_Suburb", 24.0 },
                { "Commercial_City", 80.0 },
                { "Commercial_Suburb", 30.0 },
                { "Mixed_City", 75.0 },
                { "Mixed_Suburb", 27.0 },
                { "Industrial_City", 45.0 },
                { "Industrial_Suburb", 45.0 }
            };

            return heights.ContainsKey(key) ? heights[key] : 24.0;
        }

        /// <summary>
        /// Calculate required parking spaces based on project type and built-up area
        /// </summary>
        /// <param name="projectType">Type of development</param>
        /// <param name="builtUpArea">Built-up area in square meters</param>
        /// <param name="dwellingUnits">Number of dwelling units (for residential)</param>
        /// <returns>Number of parking spaces required</returns>
        public static int CalculateParkingSpaces(string projectType, double builtUpArea, int dwellingUnits = 0)
        {
            switch (projectType)
            {
                case "Residential":
                    // 1 ECS per dwelling unit, or 1 per 100 sqm if units unknown
                    return dwellingUnits > 0 ? dwellingUnits : (int)Math.Ceiling(builtUpArea / 100.0);
                
                case "Commercial":
                    // 1 ECS per 100 sqm of built-up area
                    return (int)Math.Ceiling(builtUpArea / 100.0);
                
                case "Mixed":
                    // 1 ECS per 80 sqm (average of residential and commercial)
                    return (int)Math.Ceiling(builtUpArea / 80.0);
                
                case "Industrial":
                    // 1 ECS per 150 sqm
                    return (int)Math.Ceiling(builtUpArea / 150.0);
                
                default:
                    return (int)Math.Ceiling(builtUpArea / 100.0);
            }
        }

        /// <summary>
        /// Get open space requirement as percentage of plot area
        /// </summary>
        public static double GetOpenSpaceRequirement(string projectType)
        {
            var requirements = new Dictionary<string, double>
            {
                { "Residential", 15.0 },
                { "Commercial", 10.0 },
                { "Mixed", 12.0 },
                { "Industrial", 20.0 }
            };

            return requirements.ContainsKey(projectType) ? requirements[projectType] : 15.0;
        }

        /// <summary>
        /// Get minimum inter-building distance in meters
        /// </summary>
        public static double GetMinInterBuildingDistance(string projectType)
        {
            var distances = new Dictionary<string, double>
            {
                { "Residential", 6.0 },
                { "Commercial", 4.5 },
                { "Mixed", 5.0 },
                { "Industrial", 6.0 }
            };

            return distances.ContainsKey(projectType) ? distances[projectType] : 6.0;
        }

        /// <summary>
        /// Get FSI (Floor Space Index) limits
        /// </summary>
        public static double GetFSILimit(string projectType, string zoneType)
        {
            var key = $"{projectType}_{zoneType}";
            var fsiLimits = new Dictionary<string, double>
            {
                { "Residential_City", 3.0 },
                { "Residential_Suburb", 2.5 },
                { "Commercial_City", 5.0 },
                { "Commercial_Suburb", 4.0 },
                { "Mixed_City", 4.0 },
                { "Mixed_Suburb", 3.0 }
            };

            return fsiLimits.ContainsKey(key) ? fsiLimits[key] : 2.0;
        }
    }
}
