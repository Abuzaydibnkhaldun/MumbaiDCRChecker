using Autodesk.Revit.DB;
using MumbaiDCRChecker.Models;
using System;
using System.Linq;

namespace MumbaiDCRChecker.Services
{
    public class HeightChecker
    {
        private readonly Document _doc;
        private const double FEET_TO_METERS = 0.3048;

        public HeightChecker(Document doc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        }

        public ComplianceResult CheckHeight(DCRParameters dcrParams)
        {
            try
            {
                var groundLevel = GetLevelByName(dcrParams.GroundLevelName);
                var topLevel = GetLevelByName(dcrParams.TopLevelName);

                if (groundLevel == null || topLevel == null)
                {
                    return new ComplianceResult
                    {
                        Parameter = "Building Height",
                        Required = $"≤ {dcrParams.MaxBuildingHeight:F2} m",
                        Actual = "N/A",
                        Status = "WARNING",
                        Details = "Could not find specified levels"
                    };
                }

                double heightFeet = topLevel.Elevation - groundLevel.Elevation;
                double heightMeters = heightFeet * FEET_TO_METERS;
                bool passed = heightMeters <= dcrParams.MaxBuildingHeight + 0.01;

                return new ComplianceResult
                {
                    Parameter = "Building Height",
                    Required = $"≤ {dcrParams.MaxBuildingHeight:F2} m",
                    Actual = $"{heightMeters:F2} m",
                    Status = passed ? "PASS" : "FAIL",
                    Details = passed
                        ? "Compliant"
                        : $"Exceeds limit by {(heightMeters - dcrParams.MaxBuildingHeight):F2} m"
                };
            }
            catch (Exception ex)
            {
                return new ComplianceResult
                {
                    Parameter = "Building Height",
                    Required = $"≤ {dcrParams.MaxBuildingHeight:F2} m",
                    Actual = "Error",
                    Status = "WARNING",
                    Details = $"Calculation error: {ex.Message}"
                };
            }
        }

        private Level GetLevelByName(string levelName)
        {
            FilteredElementCollector collector = new FilteredElementCollector(_doc);
            return collector.OfClass(typeof(Level))
                           .Cast<Level>()
                           .FirstOrDefault(l => l.Name == levelName);
        }
    }
}