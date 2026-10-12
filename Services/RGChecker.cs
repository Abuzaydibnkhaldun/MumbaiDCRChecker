using Autodesk.Revit.DB;
using MumbaiDCRChecker.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MumbaiDCRChecker.Services
{
    /// <summary>
    /// Phase 2 Feature: Recreational Ground (RG) / Layout Open Space (LOS) Checker
    /// DCPR 2034 Regulation 27
    /// 
    /// Applicability: Plots >= 2,125 sqm
    /// Requirements:
    ///   - 10% of plot area (residential/commercial), 15% (industrial)
    ///   - Main pocket >= 750 sqm
    ///   - Other pockets >= 125 sqm each
    ///   - Min dimension >= 7.5m
    ///   - L/B ratio < 2.5 (unless width > 16.6m)
    ///   - Ground level >= 60% of total RG
    ///   - Mother earth (unpaved) >= 30% of total RG
    /// 
    /// Detection: Areas or Rooms named with "RG", "Recreational", "LOS" keywords
    /// </summary>
    public class RGChecker
    {
        private readonly Document _doc;
        private const double SQFT_TO_SQM = 0.092903;
        private const double FEET_TO_METERS = 0.3048;
        private const double RG_THRESHOLD_SQM = 2125.0;

        public RGChecker(Document doc)
        {
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
        }

        /// <summary>
        /// Run all RG compliance checks. Returns a list of results.
        /// </summary>
        public List<ComplianceResult> CheckRG(DCRParameters dcrParams, double plotAreaSqm)
        {
            var results = new List<ComplianceResult>();

            try
            {
                // Step 1: Check if RG is required
                if (plotAreaSqm < RG_THRESHOLD_SQM)
                {
                    results.Add(new ComplianceResult
                    {
                        Parameter = "Recreational Ground",
                        Required = "N/A",
                        Actual = "N/A",
                        Status = "PASS",
                        Details = string.Format(
                            "Not required - plot area {0:F0} sqm < 2,125 sqm threshold",
                            plotAreaSqm)
                    });
                    return results;
                }

                // Step 2: Calculate required RG area
                double requiredPercent = GetRequiredRGPercent(dcrParams.ProjectType);
                double requiredAreaSqm = plotAreaSqm * requiredPercent / 100.0;

                // Step 3: Find RG areas in model (Areas or Rooms with RG keywords)
                var rgPockets = FindRGPockets();

                if (rgPockets.Count == 0)
                {
                    results.Add(new ComplianceResult
                    {
                        Parameter = "RG Total Area",
                        Required = string.Format(">= {0:F1} sqm ({1}%)", requiredAreaSqm, requiredPercent),
                        Actual = "Not found",
                        Status = "WARNING",
                        Details = string.Format(
                            "No RG areas detected. Tag Areas/Rooms with 'RG', 'Recreational', or 'LOS' in their name.\n" +
                            "Plot area: {0:F0} sqm, Required: {1:F1}% = {2:F1} sqm",
                            plotAreaSqm, requiredPercent, requiredAreaSqm)
                    });
                    return results;
                }

                // Step 4: Calculate total RG area
                double totalRGSqm = 0;
                foreach (var pocket in rgPockets)
                {
                    totalRGSqm += pocket.AreaSqm;
                }

                // Step 5: Check total area
                bool totalPassed = totalRGSqm >= requiredAreaSqm - 0.1;
                results.Add(new ComplianceResult
                {
                    Parameter = "RG Total Area",
                    Required = string.Format(">= {0:F1} sqm ({1}%)", requiredAreaSqm, requiredPercent),
                    Actual = string.Format("{0:F1} sqm ({1:F1}%)", totalRGSqm, (totalRGSqm / plotAreaSqm) * 100),
                    Status = totalPassed ? "PASS" : "FAIL",
                    Details = totalPassed
                        ? string.Format("Compliant - {0} pocket(s) found", rgPockets.Count)
                        : string.Format("Deficient by {0:F1} sqm", requiredAreaSqm - totalRGSqm)
                });

                // Step 6: Sort pockets by area (largest first = main pocket)
                var sortedPockets = rgPockets.OrderByDescending(p => p.AreaSqm).ToList();
                RGPocketInfo mainPocket = sortedPockets[0];

                // Step 7: Check main pocket >= 750 sqm
                bool mainPassed = mainPocket.AreaSqm >= 750.0 - 0.1;
                results.Add(new ComplianceResult
                {
                    Parameter = "RG Main Pocket",
                    Required = ">= 750 sqm",
                    Actual = string.Format("{0:F1} sqm", mainPocket.AreaSqm),
                    Status = mainPassed ? "PASS" : "FAIL",
                    Details = mainPassed
                        ? string.Format("Compliant - '{0}'", mainPocket.Name)
                        : string.Format("Main pocket '{0}' is {1:F1} sqm short",
                            mainPocket.Name, 750.0 - mainPocket.AreaSqm)
                });

                // Step 8: Check other pockets >= 125 sqm each
                if (sortedPockets.Count > 1)
                {
                    var smallPockets = new List<string>();
                    for (int i = 1; i < sortedPockets.Count; i++)
                    {
                        if (sortedPockets[i].AreaSqm < 125.0 - 0.1)
                        {
                            smallPockets.Add(string.Format("'{0}' ({1:F1} sqm)",
                                sortedPockets[i].Name, sortedPockets[i].AreaSqm));
                        }
                    }

                    bool othersPassed = smallPockets.Count == 0;
                    results.Add(new ComplianceResult
                    {
                        Parameter = "RG Other Pockets",
                        Required = ">= 125 sqm each",
                        Actual = string.Format("{0} pocket(s)", sortedPockets.Count - 1),
                        Status = othersPassed ? "PASS" : "FAIL",
                        Details = othersPassed
                            ? "All secondary pockets meet minimum"
                            : "Undersized: " + string.Join(", ", smallPockets)
                    });
                }

                // Step 9: Check dimensions of main pocket
                CheckPocketDimensions(mainPocket, results);

                // Step 10: Check ground level percentage
                CheckGroundLevel(sortedPockets, totalRGSqm, dcrParams.GroundLevelName, results);
            }
            catch (Exception ex)
            {
                results.Add(new ComplianceResult
                {
                    Parameter = "RG Check",
                    Required = "N/A",
                    Actual = "Error",
                    Status = "WARNING",
                    Details = "Error during RG check: " + ex.Message
                });
            }

            return results;
        }

        // ============================================================
        // RG pocket detection - find Areas or Rooms tagged as RG
        // ============================================================

        private List<RGPocketInfo> FindRGPockets()
        {
            var pockets = new List<RGPocketInfo>();

            // Try Areas first
            try
            {
                var areaCollector = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_Areas)
                    .WhereElementIsNotElementType();

                foreach (Element elem in areaCollector)
                {
                    Area area = elem as Area;
                    if (area == null) continue;
                    if (area.Area <= 0) continue;

                    string name = (area.Name ?? "").Trim();
                    if (!IsRGName(name)) continue;

                    BoundingBoxXYZ bbox = area.get_BoundingBox(null);
                    double areaSqm = area.Area * SQFT_TO_SQM;

                    double lengthM = 0;
                    double widthM = 0;
                    if (bbox != null)
                    {
                        lengthM = (bbox.Max.X - bbox.Min.X) * FEET_TO_METERS;
                        widthM = (bbox.Max.Y - bbox.Min.Y) * FEET_TO_METERS;
                    }

                    Level areaLevel = _doc.GetElement(area.LevelId) as Level;

                    pockets.Add(new RGPocketInfo
                    {
                        Name = name,
                        AreaSqm = areaSqm,
                        LengthM = Math.Max(lengthM, widthM),
                        WidthM = Math.Min(lengthM, widthM),
                        LevelName = areaLevel != null ? areaLevel.Name : "Unknown",
                        ElementId = area.Id
                    });
                }
            }
            catch { /* Areas may not be available */ }

            // Also try Rooms
            try
            {
                var roomCollector = new FilteredElementCollector(_doc)
                    .OfCategory(BuiltInCategory.OST_Rooms)
                    .WhereElementIsNotElementType();

                foreach (Element elem in roomCollector)
                {
                    SpatialElement room = elem as SpatialElement;
                    if (room == null) continue;

                    // Get area from parameter
                    Parameter areaParam = room.get_Parameter(BuiltInParameter.ROOM_AREA);
                    if (areaParam == null) continue;
                    double roomAreaSqFt = areaParam.AsDouble();
                    if (roomAreaSqFt <= 0) continue;

                    string name = (room.Name ?? "").Trim();
                    if (!IsRGName(name)) continue;

                    BoundingBoxXYZ bbox = room.get_BoundingBox(null);
                    double areaSqm = roomAreaSqFt * SQFT_TO_SQM;

                    double lengthM = 0;
                    double widthM = 0;
                    if (bbox != null)
                    {
                        lengthM = (bbox.Max.X - bbox.Min.X) * FEET_TO_METERS;
                        widthM = (bbox.Max.Y - bbox.Min.Y) * FEET_TO_METERS;
                    }

                    Level roomLevel = _doc.GetElement(room.LevelId) as Level;

                    pockets.Add(new RGPocketInfo
                    {
                        Name = name,
                        AreaSqm = areaSqm,
                        LengthM = Math.Max(lengthM, widthM),
                        WidthM = Math.Min(lengthM, widthM),
                        LevelName = roomLevel != null ? roomLevel.Name : "Unknown",
                        ElementId = room.Id
                    });
                }
            }
            catch { /* Rooms may not be available */ }

            return pockets;
        }

        /// <summary>
        /// Check if a name indicates an RG area (case-insensitive)
        /// </summary>
        private bool IsRGName(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string lower = name.ToLower();

            return lower.Contains("rg")
                || lower.Contains("recreational")
                || lower.Contains("los")
                || lower.Contains("layout open space")
                || lower.Contains("recreation ground");
        }

        // ============================================================
        // Dimension checks
        // ============================================================

        private void CheckPocketDimensions(RGPocketInfo pocket, List<ComplianceResult> results)
        {
            // Min dimension >= 7.5m
            double minDim = pocket.WidthM;
            bool minDimPassed = minDim >= 7.5 - 0.01;

            results.Add(new ComplianceResult
            {
                Parameter = "RG Min Dimension",
                Required = ">= 7.5 m",
                Actual = string.Format("{0:F1} m", minDim),
                Status = minDimPassed ? "PASS" : (minDim > 0.1 ? "FAIL" : "WARNING"),
                Details = minDimPassed
                    ? string.Format("Compliant (main pocket '{0}')", pocket.Name)
                    : minDim > 0.1
                        ? string.Format("Main pocket narrowest side {0:F1}m < 7.5m required", minDim)
                        : "Could not determine dimensions from bounding box"
            });

            // L/B ratio < 2.5 (unless width > 16.6m)
            if (pocket.WidthM > 0.1 && pocket.LengthM > 0.1)
            {
                double lbRatio = pocket.LengthM / pocket.WidthM;
                bool ratioPassed = lbRatio < 2.5 || pocket.WidthM > 16.6;

                string detail;
                if (ratioPassed && pocket.WidthM > 16.6)
                {
                    detail = string.Format("Width {0:F1}m > 16.6m, ratio exemption applies", pocket.WidthM);
                }
                else if (ratioPassed)
                {
                    detail = string.Format("L={0:F1}m x W={1:F1}m, Ratio={2:F2}",
                        pocket.LengthM, pocket.WidthM, lbRatio);
                }
                else
                {
                    detail = string.Format("L={0:F1}m x W={1:F1}m, Ratio={2:F2} exceeds 2.5",
                        pocket.LengthM, pocket.WidthM, lbRatio);
                }

                results.Add(new ComplianceResult
                {
                    Parameter = "RG L/B Ratio",
                    Required = "< 2.5",
                    Actual = string.Format("{0:F2}", lbRatio),
                    Status = ratioPassed ? "PASS" : "FAIL",
                    Details = detail
                });
            }
        }

        // ============================================================
        // Ground level check (>= 60% of total RG at ground)
        // ============================================================

        private void CheckGroundLevel(
            List<RGPocketInfo> pockets,
            double totalRGSqm,
            string groundLevelName,
            List<ComplianceResult> results)
        {
            if (totalRGSqm <= 0) return;

            // Find ground level
            Level groundLevel = null;
            if (!string.IsNullOrEmpty(groundLevelName))
            {
                groundLevel = new FilteredElementCollector(_doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .FirstOrDefault(l => l.Name == groundLevelName);
            }

            if (groundLevel == null)
            {
                // Fall back to lowest level
                groundLevel = new FilteredElementCollector(_doc)
                    .OfClass(typeof(Level))
                    .Cast<Level>()
                    .OrderBy(l => l.Elevation)
                    .FirstOrDefault();
            }

            if (groundLevel == null)
            {
                results.Add(new ComplianceResult
                {
                    Parameter = "RG Ground Level",
                    Required = ">= 60%",
                    Actual = "N/A",
                    Status = "WARNING",
                    Details = "Cannot determine ground level for RG distribution check"
                });
                return;
            }

            // Sum area of pockets at ground level
            double groundRGSqm = 0;

            foreach (var pocket in pockets)
            {
                if (pocket.LevelName == groundLevel.Name)
                {
                    groundRGSqm += pocket.AreaSqm;
                }
            }

            double groundPercent = (groundRGSqm / totalRGSqm) * 100.0;
            bool passed = groundPercent >= 60.0 - 0.1;

            results.Add(new ComplianceResult
            {
                Parameter = "RG at Ground Level",
                Required = ">= 60%",
                Actual = string.Format("{0:F1}% ({1:F1} sqm)", groundPercent, groundRGSqm),
                Status = passed ? "PASS" : "FAIL",
                Details = passed
                    ? string.Format("Compliant - {0:F1} sqm at '{1}'", groundRGSqm, groundLevel.Name)
                    : string.Format("Only {0:F1}% at ground (need 60%). Ground = '{1}'",
                        groundPercent, groundLevel.Name)
            });

            // Mother earth check (30% unpaved) - informational only
            results.Add(new ComplianceResult
            {
                Parameter = "RG Mother Earth",
                Required = ">= 30%",
                Actual = "Manual verification",
                Status = "WARNING",
                Details = string.Format(
                    "Min {0:F1} sqm must be unpaved/natural ground. Verify manually.",
                    totalRGSqm * 0.30)
            });
        }

        // ============================================================
        // Helpers
        // ============================================================

        private double GetRequiredRGPercent(string projectType)
        {
            if (projectType == "Industrial")
                return 15.0;
            return 10.0; // Residential, Commercial, Mixed
        }

        /// <summary>
        /// Internal data class for RG pocket information
        /// </summary>
        private class RGPocketInfo
        {
            public string Name { get; set; }
            public double AreaSqm { get; set; }
            public double LengthM { get; set; }
            public double WidthM { get; set; }
            public string LevelName { get; set; }
            public ElementId ElementId { get; set; }
        }
    }
}