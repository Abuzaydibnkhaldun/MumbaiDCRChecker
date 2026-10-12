using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using MumbaiDCRChecker.Models;

namespace MumbaiDCRChecker.Services
{
    /// <summary>
    /// Checks staircase compliance per DCPR 2034 Regulation 37(13) - Table 16.
    /// Detects Revit Stair elements and validates:
    ///   - Minimum width (by occupancy type and building height)
    ///   - Maximum riser height
    ///   - Minimum tread depth
    ///   - Headroom clearance
    ///   - Handrail height
    ///   - Number of staircases required
    ///   - Landing width
    /// </summary>
    public class StaircaseChecker
    {
        private readonly Document _doc;

        // ── DCPR 2034 Regulation 37(13) Staircase Width Requirements (Table 16) ──
        // Residential buildings:
        //   Height ≤ 24m: 1.00m minimum
        //   Height > 24m (high-rise): 1.50m minimum
        // Other occupancies (commercial, institutional, educational):
        //   1.50m minimum
        // Assembly buildings (auditorium, theatre):
        //   2.00m minimum

        // ── Riser and Tread (Regulation 37(13)) ──
        // Max riser: 17.5cm (general), 19cm (residential high-rise), 25cm (internal dwelling unit stairway)
        // Min tread: 25cm (residential), 30cm (other occupancies)

        // ── Headroom ──
        // Min 2.2m clear headroom over any stair flight or landing

        // ── Handrail (Regulation 37(13)) ──
        // Min height: 100cm (1.0m) from nosing line

        // ── Number of Staircases ──
        // Buildings > 24m height: Min 2 staircases (one enclosed, one on external wall)
        // Single staircase OK if: floor area ≤ 300 sqm AND height ≤ 24m

        // ── Landing ──
        // Width not less than width of staircase

        // Conversion: Revit internal units are feet
        private const double FEET_TO_METERS = 0.3048;
        private const double METERS_TO_FEET = 1.0 / 0.3048;

        public StaircaseChecker(Document doc)
        {
            _doc = doc;
        }

        /// <summary>
        /// Main entry point — runs all staircase compliance checks.
        /// Groups continuous staircases (same XY footprint across levels) as one unit.
        /// </summary>
        public List<ComplianceResult> CheckStaircases(DCRParameters dcrParams)
        {
            var results = new List<ComplianceResult>();

            // Collect all Stair elements in the model
            var stairs = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Stairs)
                .WhereElementIsNotElementType()
                .Cast<Stairs>()
                .ToList();

            if (stairs.Count == 0)
            {
                results.Add(new ComplianceResult
                {
                    Parameter = "Staircase Detection",
                    Required = "At least 1 staircase",
                    Actual = "0 staircases found",
                    Status = "WARNING",
                    Details = "No Revit Stair elements detected (Architecture > Stairs). " +
                              "Ensure staircases are modeled using the Stair tool for compliance checking."
                });
                return results;
            }

            // ── Group continuous staircases by XY footprint overlap ──
            var groups = GroupContinuousStaircases(stairs);

            // Get building height for rule selection
            double buildingHeightM = GetBuildingHeight();
            string occupancyType = dcrParams.ProjectType ?? "Residential";
            bool isHighRise = buildingHeightM > 24.0;

            // ── Check 1: Number of Staircases (count GROUPS, not individual elements) ──
            results.Add(CheckStaircaseCount(groups.Count, buildingHeightM, occupancyType));

            // Detection info
            if (groups.Count != stairs.Count)
            {
                results.Add(new ComplianceResult
                {
                    Parameter = "Staircase Grouping",
                    Required = "—",
                    Actual = $"{stairs.Count} stair elements → {groups.Count} continuous staircase(s)",
                    Status = "PASS",
                    Details = "Stairs with overlapping XY footprints across levels are treated as one continuous staircase."
                });
            }

            // ── Per-group checks ──
            var allStairIds = new List<ElementId>();

            for (int gi = 0; gi < groups.Count; gi++)
            {
                var group = groups[gi];
                string groupName = GetGroupName(group, gi + 1);
                var groupIds = group.Select(s => s.Id).ToList();
                allStairIds.AddRange(groupIds);

                string levelInfo = GetGroupLevelInfo(group);
                if (!string.IsNullOrEmpty(levelInfo))
                    groupName += $" ({levelInfo})";

                // For each check, evaluate ALL stairs in the group and report worst case
                ComplianceResult result;

                // Check 2: Width — use narrowest across all stairs in group
                result = CheckWidthForGroup(group, groupName, buildingHeightM, occupancyType);
                result.ViolatingElementIds = groupIds.ToArray();
                results.Add(result);

                // Check 3: Riser Height — use tallest riser across group
                result = CheckRiserHeightForGroup(group, groupName, buildingHeightM, occupancyType);
                result.ViolatingElementIds = groupIds.ToArray();
                results.Add(result);

                // Check 4: Tread Depth — use shallowest tread across group
                result = CheckTreadDepthForGroup(group, groupName, occupancyType);
                result.ViolatingElementIds = groupIds.ToArray();
                results.Add(result);

                // Check 5: Headroom (ray-cast) — check each stair in group, report worst
                result = CheckHeadroomForGroup(group, groupName);
                // ViolatingElementIds already set inside (includes obstruction element)
                if (result.ViolatingElementIds == null)
                    result.ViolatingElementIds = groupIds.ToArray();
                results.Add(result);

                // Check 6: Handrail Height — check all railings in group
                var handrailResult = CheckHandrailHeightForGroup(group, groupName);
                if (handrailResult != null)
                    results.Add(handrailResult);

                // Check 7: Landing Width — check all landings in group
                var landingResult = CheckLandingWidthForGroup(group, groupName, buildingHeightM, occupancyType);
                if (landingResult != null)
                    results.Add(landingResult);
            }

            // Summary
            int passCount = results.Count(r => r.Status == "PASS");
            int failCount = results.Count(r => r.Status == "FAIL");
            int warnCount = results.Count(r => r.Status == "WARNING");

            var summaryResult = new ComplianceResult
            {
                Parameter = "Staircase Compliance Summary",
                Required = "All checks pass",
                Actual = $"{groups.Count} staircase(s) ({stairs.Count} elements), " +
                         $"{passCount} pass, {failCount} fail, {warnCount} warning",
                Status = failCount > 0 ? "FAIL" : (warnCount > 0 ? "WARNING" : "PASS"),
                Details = $"DCPR 2034 Reg 37(13) — {occupancyType}, " +
                          $"Building height: {buildingHeightM:F1}m" +
                          (isHighRise ? " (High-Rise)" : "")
            };
            if (failCount > 0)
                summaryResult.ViolatingElementIds = allStairIds.ToArray();
            results.Insert(0, summaryResult);

            return results;
        }

        // ════════════════════════════════════════════════════════════════════
        // STAIRCASE GROUPING
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Groups stairs that share overlapping XY footprints (continuous across levels).
        /// Two stairs overlap if their bounding boxes intersect in X and Y (ignoring Z).
        /// Uses union-find to handle transitive overlaps (A overlaps B, B overlaps C → one group).
        /// </summary>
        private List<List<Stairs>> GroupContinuousStaircases(List<Stairs> stairs)
        {
            int n = stairs.Count;
            // parent array for union-find
            int[] parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            // Get XY bounding boxes
            var boxes = new BoundingBoxXYZ[n];
            for (int i = 0; i < n; i++)
            {
                boxes[i] = stairs[i].get_BoundingBox(null);
            }

            // Check each pair for XY overlap
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (boxes[i] != null && boxes[j] != null &&
                        BoundingBoxesOverlapXY(boxes[i], boxes[j]))
                    {
                        Union(parent, i, j);
                    }
                }
            }

            // Build groups from union-find
            var groupMap = new Dictionary<int, List<Stairs>>();
            for (int i = 0; i < n; i++)
            {
                int root = Find(parent, i);
                if (!groupMap.ContainsKey(root))
                    groupMap[root] = new List<Stairs>();
                groupMap[root].Add(stairs[i]);
            }

            // Sort each group by base elevation (lowest first)
            var groups = groupMap.Values.ToList();
            foreach (var group in groups)
            {
                group.Sort((a, b) =>
                {
                    var bboxA = a.get_BoundingBox(null);
                    var bboxB = b.get_BoundingBox(null);
                    double zA = bboxA != null ? bboxA.Min.Z : 0;
                    double zB = bboxB != null ? bboxB.Min.Z : 0;
                    return zA.CompareTo(zB);
                });
            }

            return groups;
        }

        /// <summary>
        /// Checks if two bounding boxes overlap in the XY plane (ignoring Z).
        /// Uses a tolerance to catch stairs that are almost aligned.
        /// </summary>
        private bool BoundingBoxesOverlapXY(BoundingBoxXYZ a, BoundingBoxXYZ b)
        {
            // Tolerance: 0.5 feet (~150mm) to catch slight misalignments
            double tol = 0.5;

            bool xOverlap = a.Min.X < (b.Max.X + tol) && (a.Max.X + tol) > b.Min.X;
            bool yOverlap = a.Min.Y < (b.Max.Y + tol) && (a.Max.Y + tol) > b.Min.Y;

            return xOverlap && yOverlap;
        }

        // Union-Find helpers
        private int Find(int[] parent, int i)
        {
            while (parent[i] != i)
            {
                parent[i] = parent[parent[i]]; // path compression
                i = parent[i];
            }
            return i;
        }

        private void Union(int[] parent, int i, int j)
        {
            int ri = Find(parent, i);
            int rj = Find(parent, j);
            if (ri != rj) parent[ri] = rj;
        }

        /// <summary>
        /// Gets a display name for a staircase group.
        /// </summary>
        private string GetGroupName(List<Stairs> group, int groupIndex)
        {
            if (group.Count == 1)
                return GetStairName(group[0], groupIndex);

            // Try to get a common Mark from the first stair
            var markParam = group[0].get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            if (markParam != null && markParam.HasValue && !string.IsNullOrWhiteSpace(markParam.AsString()))
                return $"Staircase '{markParam.AsString()}'";

            return $"Staircase {groupIndex}";
        }

        /// <summary>
        /// Gets level info string for a group (e.g. "L0 → L3").
        /// </summary>
        private string GetGroupLevelInfo(List<Stairs> group)
        {
            var levelNames = new List<string>();
            foreach (var stair in group)
            {
                // Base level
                var baseLevelParam = stair.get_Parameter(BuiltInParameter.STAIRS_BASE_LEVEL_PARAM);
                if (baseLevelParam != null && baseLevelParam.HasValue)
                {
                    var level = _doc.GetElement(baseLevelParam.AsElementId()) as Level;
                    if (level != null && !levelNames.Contains(level.Name))
                        levelNames.Add(level.Name);
                }
                // Top level
                var topLevelParam = stair.get_Parameter(BuiltInParameter.STAIRS_TOP_LEVEL_PARAM);
                if (topLevelParam != null && topLevelParam.HasValue)
                {
                    var level = _doc.GetElement(topLevelParam.AsElementId()) as Level;
                    if (level != null && !levelNames.Contains(level.Name))
                        levelNames.Add(level.Name);
                }
            }

            if (levelNames.Count >= 2)
                return $"{levelNames.First()} → {levelNames.Last()}";
            if (levelNames.Count == 1)
                return levelNames[0];
            return "";
        }

        // ════════════════════════════════════════════════════════════════════
        // GROUP-LEVEL CHECK METHODS (worst-case across all stairs in group)
        // ════════════════════════════════════════════════════════════════════

        /// <summary>Width check for group — reports narrowest stair in group.</summary>
        private ComplianceResult CheckWidthForGroup(List<Stairs> group, string groupName,
            double buildingHeightM, string occupancyType)
        {
            ComplianceResult worstResult = null;
            double worstWidthM = double.MaxValue;

            foreach (var stair in group)
            {
                var result = CheckWidth(stair, groupName, buildingHeightM, occupancyType);
                // Parse actual width from result or just track worst status
                if (result.Status == "FAIL" || result.Status == "WARNING" || worstResult == null)
                {
                    // Try to extract width value
                    double w = ExtractFirstNumber(result.Actual);
                    if (w < worstWidthM || worstResult == null)
                    {
                        worstWidthM = w;
                        worstResult = result;
                    }
                }
            }

            if (worstResult != null)
            {
                worstResult.Parameter = $"{groupName}: Width";
                if (group.Count > 1)
                    worstResult.Details += $" [worst of {group.Count} stair segments]";
            }
            return worstResult;
        }

        /// <summary>Riser check for group — reports tallest riser in group.</summary>
        private ComplianceResult CheckRiserHeightForGroup(List<Stairs> group, string groupName,
            double buildingHeightM, string occupancyType)
        {
            ComplianceResult worstResult = null;
            double worstRiserMm = 0;

            foreach (var stair in group)
            {
                var result = CheckRiserHeight(stair, groupName, buildingHeightM, occupancyType);
                double r = ExtractFirstNumber(result.Actual);
                if (r > worstRiserMm || worstResult == null)
                {
                    worstRiserMm = r;
                    worstResult = result;
                }
            }

            if (worstResult != null)
            {
                worstResult.Parameter = $"{groupName}: Riser Height";
                if (group.Count > 1)
                    worstResult.Details += $" [worst of {group.Count} stair segments]";
            }
            return worstResult;
        }

        /// <summary>Tread check for group — reports shallowest tread in group.</summary>
        private ComplianceResult CheckTreadDepthForGroup(List<Stairs> group, string groupName,
            string occupancyType)
        {
            ComplianceResult worstResult = null;
            double worstTreadMm = double.MaxValue;

            foreach (var stair in group)
            {
                var result = CheckTreadDepth(stair, groupName, occupancyType);
                double t = ExtractFirstNumber(result.Actual);
                if (t < worstTreadMm || worstResult == null)
                {
                    worstTreadMm = t;
                    worstResult = result;
                }
            }

            if (worstResult != null)
            {
                worstResult.Parameter = $"{groupName}: Tread Depth";
                if (group.Count > 1)
                    worstResult.Details += $" [worst of {group.Count} stair segments]";
            }
            return worstResult;
        }

        /// <summary>Headroom check for group — reports worst headroom across all stairs.</summary>
        private ComplianceResult CheckHeadroomForGroup(List<Stairs> group, string groupName)
        {
            ComplianceResult worstResult = null;
            double worstClearanceM = double.MaxValue;

            foreach (var stair in group)
            {
                var result = CheckHeadroom(stair, group, groupName);
                double c = ExtractFirstNumber(result.Actual);
                if ((c < worstClearanceM && c > 0) || worstResult == null ||
                    (result.Status == "FAIL" && worstResult.Status != "FAIL"))
                {
                    worstClearanceM = c;
                    worstResult = result;
                }
            }

            if (worstResult != null)
            {
                worstResult.Parameter = $"{groupName}: Headroom";
                if (group.Count > 1)
                    worstResult.Details += $" [worst of {group.Count} stair segments]";
            }
            return worstResult;
        }

        /// <summary>Handrail check for group — reports shortest handrail.</summary>
        private ComplianceResult CheckHandrailHeightForGroup(List<Stairs> group, string groupName)
        {
            ComplianceResult worstResult = null;
            double worstHeightM = double.MaxValue;
            var allRailingIds = new List<ElementId>();

            foreach (var stair in group)
            {
                var result = CheckHandrailHeight(stair, groupName);
                if (result == null) continue;

                var railingIds = stair.GetAssociatedRailings();
                if (railingIds != null) allRailingIds.AddRange(railingIds);

                double h = ExtractFirstNumber(result.Actual);
                if ((h < worstHeightM && h > 0) || worstResult == null ||
                    (result.Status == "FAIL" && worstResult.Status != "FAIL"))
                {
                    worstHeightM = h;
                    worstResult = result;
                }
            }

            if (worstResult != null)
            {
                worstResult.Parameter = $"{groupName}: Handrail Height";
                if (allRailingIds.Count > 0)
                    worstResult.ViolatingElementIds = allRailingIds.ToArray();
                else
                    worstResult.ViolatingElementIds = group.Select(s => s.Id).ToArray();
                if (group.Count > 1)
                    worstResult.Details += $" [worst of {group.Count} stair segments]";
            }
            return worstResult;
        }

        /// <summary>Landing check for group — reports narrowest landing.</summary>
        private ComplianceResult CheckLandingWidthForGroup(List<Stairs> group, string groupName,
            double buildingHeightM, string occupancyType)
        {
            ComplianceResult worstResult = null;
            double worstLandingM = double.MaxValue;
            var allLandingIds = new List<ElementId>();

            foreach (var stair in group)
            {
                var result = CheckLandingWidth(stair, groupName, buildingHeightM, occupancyType);
                if (result == null) continue;

                var landingIds = stair.GetStairsLandings();
                if (landingIds != null) allLandingIds.AddRange(landingIds);

                double l = ExtractFirstNumber(result.Actual);
                if ((l < worstLandingM && l > 0) || worstResult == null ||
                    (result.Status == "FAIL" && worstResult.Status != "FAIL"))
                {
                    worstLandingM = l;
                    worstResult = result;
                }
            }

            if (worstResult != null)
            {
                worstResult.Parameter = $"{groupName}: Landing Width";
                if (allLandingIds.Count > 0)
                    worstResult.ViolatingElementIds = allLandingIds.ToArray();
                else
                    worstResult.ViolatingElementIds = group.Select(s => s.Id).ToArray();
                if (group.Count > 1)
                    worstResult.Details += $" [worst of {group.Count} stair segments]";
            }
            return worstResult;
        }

        /// <summary>
        /// Extracts the first numeric value from a string like "1.50m" or "162mm" or "Not measurable".
        /// Returns double.MaxValue if no number found.
        /// </summary>
        private double ExtractFirstNumber(string text)
        {
            if (string.IsNullOrEmpty(text)) return double.MaxValue;
            string numStr = "";
            bool foundDigit = false;
            foreach (char c in text)
            {
                if (char.IsDigit(c) || c == '.')
                {
                    numStr += c;
                    foundDigit = true;
                }
                else if (foundDigit)
                {
                    break;
                }
            }
            if (double.TryParse(numStr, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double val))
                return val;
            return double.MaxValue;
        }

        // ════════════════════════════════════════════════════════════════════
        // CHECK METHODS
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Check 1: Number of staircases required.
        /// Buildings > 24m: Min 2 (one enclosed, one on external wall)
        /// Single staircase allowed if floor area ≤ 300 sqm AND height ≤ 24m
        /// </summary>
        private ComplianceResult CheckStaircaseCount(int count, double buildingHeightM, string occupancyType)
        {
            int required;
            string ruleNote;

            if (buildingHeightM > 24.0)
            {
                required = 2;
                ruleNote = "Buildings > 24m height require min 2 staircases " +
                           "(one enclosed type, one on external wall) — Reg 37(13)";
            }
            else
            {
                required = 1;
                ruleNote = "Buildings ≤ 24m height: min 1 staircase. " +
                           "Single staircase OK if floor area ≤ 300 sqm — Reg 37(13)";
            }

            bool pass = count >= required;

            return new ComplianceResult
            {
                
                Parameter = "Number of Staircases",
                Required = $">= {required}",
                Actual = count.ToString(),
                Status = pass ? "PASS" : "FAIL",
                Details = (pass ? "Compliant" : "VIOLATION") + " — " + ruleNote
            };
        }

        /// <summary>
        /// Check 2: Minimum staircase width.
        /// Residential ≤ 24m: 1.0m
        /// Residential > 24m (high-rise): 1.5m
        /// Commercial/Other: 1.5m
        /// Assembly: 2.0m
        /// </summary>
        private ComplianceResult CheckWidth(Stairs stair, string stairName,
            double buildingHeightM, string occupancyType)
        {
            double requiredM = GetRequiredWidth(buildingHeightM, occupancyType);

            // Revit Stairs class has no .Width property.
            // Width must be read from StairsRun sub-elements or stair parameters.
            double actualFt = 0;

            // Method 1: Get width from StairsRun elements
            var runIds = stair.GetStairsRuns();
            if (runIds != null && runIds.Count > 0)
            {
                foreach (var runId in runIds)
                {
                    var run = _doc.GetElement(runId) as StairsRun;
                    if (run == null) continue;

                    // Try "Actual Run Width" or "Width" parameter on the run
                    foreach (Parameter p in run.Parameters)
                    {
                        string pName = p.Definition.Name.ToLowerInvariant();
                        if ((pName.Contains("width") || pName.Contains("run width")) &&
                            p.StorageType == StorageType.Double && p.HasValue)
                        {
                            double val = p.AsDouble();
                            if (val > 0 && (actualFt == 0 || val < actualFt))
                            {
                                actualFt = val; // Use narrowest run width
                            }
                        }
                    }
                }
            }

            // Method 2: Fallback — try parameters on the stair element itself
            if (actualFt <= 0)
            {
                foreach (Parameter p in stair.Parameters)
                {
                    string pName = p.Definition.Name.ToLowerInvariant();
                    if (pName.Contains("width") && !pName.Contains("landing") &&
                        p.StorageType == StorageType.Double && p.HasValue)
                    {
                        double val = p.AsDouble();
                        if (val > 0)
                        {
                            actualFt = val;
                            break;
                        }
                    }
                }
            }

            if (actualFt <= 0)
            {
                return new ComplianceResult
                {
                    Parameter = $"{stairName}: Width",
                    Required = $">= {requiredM:F2}m",
                    Actual = "Not measurable",
                    Status = "WARNING",
                    Details = "Could not extract stair width from Revit parameters. Verify manually."
                };
            }

            double actualM = actualFt * FEET_TO_METERS;
            bool pass = actualM >= (requiredM - 0.01); // 1cm tolerance

            string ruleExplain = GetWidthRuleExplanation(buildingHeightM, occupancyType);

            return new ComplianceResult
            {
                
                Parameter = $"{stairName}: Width",
                Required = $">= {requiredM:F2}m",
                Actual = $"{actualM:F2}m",
                Status = pass ? "PASS" : "FAIL",
                Details = (pass ? "Compliant" : $"VIOLATION — short by {(requiredM - actualM):F2}m") +
                          $" — {ruleExplain}"
            };
        }

        /// <summary>
        /// Check 3: Maximum riser height.
        /// General: max 175mm (17.5cm)
        /// Residential high-rise (>24m): max 190mm (19cm) — slightly relaxed
        /// Internal dwelling unit stairway: max 250mm (25cm) — only for duplex internal stairs
        /// </summary>
        private ComplianceResult CheckRiserHeight(Stairs stair, string stairName,
            double buildingHeightM, string occupancyType)
        {
            // Max riser depends on building type
            double maxRiserM;
            string ruleNote;

            bool isResidentialHighRise = occupancyType.IndexOf("Residential",
                StringComparison.OrdinalIgnoreCase) >= 0 && buildingHeightM > 24.0;

            if (isResidentialHighRise)
            {
                maxRiserM = 0.190; // 190mm for residential high-rise
                ruleNote = "Residential high-rise: max 190mm riser — Reg 37(13)";
            }
            else
            {
                maxRiserM = 0.175; // 175mm general
                ruleNote = "General: max 175mm riser — Reg 37(13)";
            }

            // ActualRiserHeight is in feet (internal units)
            double actualRiserFt = stair.ActualRiserHeight;
            double actualRiserM = actualRiserFt * FEET_TO_METERS;

            bool pass = actualRiserM <= (maxRiserM + 0.005); // 5mm tolerance

            return new ComplianceResult
            {
                
                Parameter = $"{stairName}: Riser Height",
                Required = $"<= {(maxRiserM * 1000):F0}mm",
                Actual = $"{(actualRiserM * 1000):F0}mm",
                Status = pass ? "PASS" : "FAIL",
                Details = (pass ? "Compliant" : $"VIOLATION — exceeds by {((actualRiserM - maxRiserM) * 1000):F0}mm") +
                          $" — {ruleNote}"
            };
        }

        /// <summary>
        /// Check 4: Minimum tread depth.
        /// Residential: min 250mm (25cm)
        /// Other occupancies: min 300mm (30cm)
        /// </summary>
        private ComplianceResult CheckTreadDepth(Stairs stair, string stairName, string occupancyType)
        {
            double minTreadM;
            string ruleNote;

            bool isResidential = occupancyType.IndexOf("Residential",
                StringComparison.OrdinalIgnoreCase) >= 0;

            if (isResidential)
            {
                minTreadM = 0.250; // 250mm residential
                ruleNote = "Residential: min 250mm tread — Reg 37(13)";
            }
            else
            {
                minTreadM = 0.300; // 300mm other
                ruleNote = "Non-residential: min 300mm tread — Reg 37(13)";
            }

            // ActualTreadDepth is in feet (internal units)
            double actualTreadFt = stair.ActualTreadDepth;
            double actualTreadM = actualTreadFt * FEET_TO_METERS;

            bool pass = actualTreadM >= (minTreadM - 0.005); // 5mm tolerance

            return new ComplianceResult
            {
                
                Parameter = $"{stairName}: Tread Depth",
                Required = $">= {(minTreadM * 1000):F0}mm",
                Actual = $"{(actualTreadM * 1000):F0}mm",
                Status = pass ? "PASS" : "FAIL",
                Details = (pass ? "Compliant" : $"VIOLATION — short by {((minTreadM - actualTreadM) * 1000):F0}mm") +
                          $" — {ruleNote}"
            };
        }

        /// <summary>
        /// Check 5: Minimum headroom clearance (2.2m).
        /// Uses ray casting — projects vertical rays (Z+) from tread surfaces of each run
        /// to detect intersections with floor slabs, landings, other stair runs, or ceilings above.
        /// The shortest vertical distance found = actual headroom.
        /// Requires a 3D view in the project for ReferenceIntersector.
        /// 
        /// FIX v2: (a) Ray offset (200mm) is added back to proximity for accurate measurement.
        ///         (b) Self-hit filter checks all stairs in the group, not just the current stair.
        ///         (c) Category+XY overlap backup filter for stringer/support sub-elements.
        ///         (d) Reports worst point as "Run X, Tread Y on LevelName" instead of XYZ coordinates.
        /// </summary>
        private ComplianceResult CheckHeadroom(Stairs stair, List<Stairs> group, string stairName)
        {
            double requiredM = 2.20; // 2200mm minimum headroom
            double requiredFt = requiredM * METERS_TO_FEET;

            // ── Step 1: Get or find a 3D view (required by ReferenceIntersector) ──
            View3D view3D = new FilteredElementCollector(_doc)
                .OfClass(typeof(View3D))
                .Cast<View3D>()
                .FirstOrDefault(v => !v.IsTemplate);

            if (view3D == null)
            {
                return new ComplianceResult
                {
                    Parameter = $"{stairName}: Headroom",
                    Required = $">= {requiredM:F2}m",
                    Actual = "No 3D view",
                    Status = "WARNING",
                    Details = "Ray-cast headroom check requires a 3D view in the project. " +
                              "Create a default 3D view (View > 3D View > Default) and re-run."
                };
            }

            // ── Step 2: Set up ReferenceIntersector to detect obstructions above ──
            var obstructionCategories = new List<BuiltInCategory>
            {
                BuiltInCategory.OST_Floors,
                BuiltInCategory.OST_Stairs,
                BuiltInCategory.OST_Ceilings,
                BuiltInCategory.OST_StructuralFraming,
                BuiltInCategory.OST_Roofs
            };

            var categoryFilter = new ElementMulticategoryFilter(obstructionCategories);
            var intersector = new ReferenceIntersector(categoryFilter, FindReferenceTarget.Face, view3D);
            intersector.FindReferencesInRevitLinks = false;

            // ── Step 3: Build a set of ALL element IDs in the staircase group for self-hit filtering ──
            // Covers multi-element staircases (U-turn modeled as separate Stairs elements in same group)
            var groupElementIds = new HashSet<long>();
            foreach (Stairs s in group)
            {
                groupElementIds.Add(s.Id.Value);
                var sRunIds = s.GetStairsRuns();
                if (sRunIds != null)
                {
                    foreach (var rid in sRunIds)
                        groupElementIds.Add(rid.Value);
                }
                var sLandingIds = s.GetStairsLandings();
                if (sLandingIds != null)
                {
                    foreach (var lid in sLandingIds)
                        groupElementIds.Add(lid.Value);
                }
            }

            // XY bounding box of this stair — for category-based self-hit backup filter
            BoundingBoxXYZ stairBB = stair.get_BoundingBox(null);

            // ── Step 4: Get stair runs and sample points on tread surfaces ──
            var runIds = stair.GetStairsRuns();
            if (runIds == null || runIds.Count == 0)
            {
                return new ComplianceResult
                {
                    Parameter = $"{stairName}: Headroom",
                    Required = $">= {requiredM:F2}m",
                    Actual = "No runs found",
                    Status = "WARNING",
                    Details = "No StairsRun elements found in this stair. Cannot perform headroom ray cast."
                };
            }

            // Ray offset: 200mm above tread surface to clear nosing/tread geometry.
            // IMPORTANT: This offset is ADDED BACK to the measured proximity to get true headroom.
            double rayOffsetFt = 200.0 / 304.8; // 200mm in feet

            double minClearanceFt = double.MaxValue;
            int totalRaysCast = 0;
            int hitsFound = 0;
            XYZ worstPoint = null;
            ElementId worstObstructionId = null;
            int worstRunIndex = -1;      // 1-based run index
            int worstTreadIndex = -1;    // 1-based tread number

            int runCounter = 0;
            foreach (var runId in runIds)
            {
                runCounter++;
                var run = _doc.GetElement(runId) as StairsRun;
                if (run == null) continue;

                // Run properties for tread number calculation
                int riserCount = run.ActualRisersNumber;
                double riserHeight = stair.ActualRiserHeight;
                BoundingBoxXYZ runBBox = run.get_BoundingBox(null);
                double runBaseZ = runBBox != null ? runBBox.Min.Z : 0;

                // Get sample points (these include the 50mm offset internally)
                var samplePoints = GetTreadSamplePoints(run, stair);

                foreach (var point in samplePoints)
                {
                    totalRaysCast++;

                    var nearest = intersector.FindNearest(point, XYZ.BasisZ);

                    if (nearest != null)
                    {
                        var hitRef = nearest.GetReference();
                        ElementId hitElementId = hitRef.ElementId;

                        // ── Self-hit filter 1: exact ID match against any element in the group ──
                        if (groupElementIds.Contains(hitElementId.Value))
                            continue;

                        // ── Self-hit filter 2: category + XY overlap backup ──
                        // Catches sub-components (stringers, supports) whose IDs aren't in run/landing lists
                        Element hitElem = _doc.GetElement(hitElementId);
                        if (hitElem != null && hitElem.Category != null &&
                            hitElem.Category.Id.Value == (long)BuiltInCategory.OST_Stairs)
                        {
                            BoundingBoxXYZ hitBB = hitElem.get_BoundingBox(null);
                            if (hitBB != null && stairBB != null)
                            {
                                // 0.5ft (~150mm) margin for XY overlap check
                                bool xOverlap = stairBB.Min.X - 0.5 <= hitBB.Max.X &&
                                                hitBB.Min.X - 0.5 <= stairBB.Max.X;
                                bool yOverlap = stairBB.Min.Y - 0.5 <= hitBB.Max.Y &&
                                                hitBB.Min.Y - 0.5 <= stairBB.Max.Y;
                                if (xOverlap && yOverlap)
                                    continue; // Same staircase area — skip
                            }
                        }

                        hitsFound++;

                        // ── FIX: Add ray offset back to proximity for accurate headroom measurement ──
                        // proximity = distance from (tread + offset) to obstruction
                        // actual headroom = distance from tread to obstruction = proximity + offset
                        double clearance = nearest.Proximity + rayOffsetFt;

                        if (clearance < minClearanceFt)
                        {
                            minClearanceFt = clearance;
                            worstPoint = point;
                            worstObstructionId = hitElementId;
                            worstRunIndex = runCounter;

                            // Calculate tread number from Z position
                            if (riserHeight > 0 && riserCount > 0)
                            {
                                double zAboveBase = point.Z - runBaseZ;
                                int treadNum = (int)Math.Round(zAboveBase / riserHeight) + 1;
                                if (treadNum < 1) treadNum = 1;
                                if (treadNum > riserCount) treadNum = riserCount;
                                worstTreadIndex = treadNum;
                            }
                            else
                            {
                                worstTreadIndex = -1;
                            }
                        }
                    }
                }
            }

            // ── Step 5: Evaluate results ──
            if (hitsFound == 0)
            {
                return new ComplianceResult
                {
                    Parameter = $"{stairName}: Headroom",
                    Required = $">= {requiredM:F2}m",
                    Actual = $"Clear — no obstruction ({totalRaysCast} rays cast)",
                    Status = "PASS",
                    Details = "No obstructions found above stair runs. " +
                              "Headroom is clear (open-to-sky or no floor slab above) — Reg 37(13)"
                };
            }

            double minClearanceM = minClearanceFt * FEET_TO_METERS;
            bool pass = minClearanceM >= (requiredM - 0.01); // 1cm tolerance

            // ── Build location string: Run X, Tread Y on Level ──
            string locationInfo = "";
            if (worstRunIndex > 0)
            {
                if (worstTreadIndex > 0)
                    locationInfo = $" | Worst point: Run {worstRunIndex}, Tread {worstTreadIndex}";
                else
                    locationInfo = $" | Worst point: Run {worstRunIndex}";
            }

            // Determine which level the worst point is on (based on Z elevation)
            if (worstPoint != null)
            {
                try
                {
                    var allLevels = new FilteredElementCollector(_doc)
                        .OfClass(typeof(Level))
                        .Cast<Level>()
                        .OrderBy(l => l.Elevation)
                        .ToList();

                    double worstZ = worstPoint.Z;
                    string closestLevelName = "";
                    double closestDist = double.MaxValue;
                    foreach (Level lev in allLevels)
                    {
                        double dist = Math.Abs(lev.Elevation - worstZ);
                        if (dist < closestDist)
                        {
                            closestDist = dist;
                            closestLevelName = lev.Name;
                        }
                    }
                    if (!string.IsNullOrEmpty(closestLevelName))
                        locationInfo += $" on {closestLevelName}";
                }
                catch { /* ignore level lookup failure */ }
            }

            // Build element IDs for highlighting
            var headroomElementIds = new List<ElementId> { stair.Id };
            if (!pass && worstObstructionId != null)
            {
                headroomElementIds.Add(worstObstructionId);

                var obstructionElem = _doc.GetElement(worstObstructionId);
                string obstructionName = obstructionElem != null
                    ? $"{obstructionElem.Category?.Name ?? "Element"} (Id: {worstObstructionId.Value})"
                    : $"Element Id: {worstObstructionId.Value}";
                locationInfo += $" | Obstruction: {obstructionName}";
            }

            return new ComplianceResult
            {
                Parameter = $"{stairName}: Headroom",
                Required = $">= {requiredM:F2}m",
                Actual = $"{minClearanceM:F2}m (min of {hitsFound} measurements)",
                Status = pass ? "PASS" : "FAIL",
                Details = (pass ? "Compliant" : $"VIOLATION — clearance short by {(requiredM - minClearanceM) * 1000:F0}mm") +
                          $" — Ray-cast from tread surfaces to nearest obstruction above" +
                          locationInfo +
                          " — Reg 37(13)",
                ViolatingElementIds = headroomElementIds.ToArray()
            };
        }

        /// <summary>
        /// Generates sample points on tread surfaces of a StairsRun for ray casting.
        /// Points are placed at each tread level along the run's walking path,
        /// offset slightly above the tread surface (+200mm) to avoid self-intersection.
        /// NOTE: CheckHeadroom adds this offset back to the measured proximity to get true headroom.
        /// </summary>
        private List<XYZ> GetTreadSamplePoints(StairsRun run, Stairs parentStair)
        {
            var points = new List<XYZ>();

            try
            {
                // Get the run's walking path (center line of the run)
                var pathCurves = run.GetStairsPath();
                if (pathCurves == null || pathCurves.NumberOfCurves() == 0)
                {
                    // Fallback: use bounding box center
                    var bbox = run.get_BoundingBox(null);
                    if (bbox != null)
                    {
                        double midX = (bbox.Min.X + bbox.Max.X) / 2.0;
                        double midY = (bbox.Min.Y + bbox.Max.Y) / 2.0;
                        // Sample at bottom, middle, and top of run
                        double offset = 200.0 / 304.8; // 200mm above tread in feet
                        points.Add(new XYZ(midX, midY, bbox.Min.Z + offset));
                        points.Add(new XYZ(midX, midY, (bbox.Min.Z + bbox.Max.Z) / 2.0 + offset));
                        points.Add(new XYZ(midX, midY, bbox.Max.Z + offset));
                    }
                    return points;
                }

                // Get run properties for tread stepping
                int riserCount = run.ActualRisersNumber;
                double riserHeight = parentStair.ActualRiserHeight;
                double treadOffset = 200.0 / 304.8; // 200mm above tread surface to avoid self-hit

                // Get the base elevation of this run
                var runBBox = run.get_BoundingBox(null);
                double baseZ = runBBox != null ? runBBox.Min.Z : 0;

                foreach (Curve pathCurve in pathCurves)
                {
                    double pathLength = pathCurve.Length;

                    // Sample a point at each tread position along the path
                    // Plus points at 1/3 and 2/3 width across the run for wider stairs
                    int numSamples = Math.Max(riserCount, 3);

                    for (int i = 0; i <= numSamples; i++)
                    {
                        double parameter = (double)i / numSamples;
                        // Clamp parameter to valid range
                        double normalizedParam = pathCurve.GetEndParameter(0) +
                            parameter * (pathCurve.GetEndParameter(1) - pathCurve.GetEndParameter(0));

                        XYZ pointOnPath = pathCurve.Evaluate(normalizedParam, false);

                        // Z position = base of run + (fraction × total rise) + offset above tread
                        double treadZ = baseZ + (parameter * riserCount * riserHeight) + treadOffset;

                        points.Add(new XYZ(pointOnPath.X, pointOnPath.Y, treadZ));
                    }
                }
            }
            catch
            {
                // If path extraction fails, fall back to bounding box sampling
                var bbox = run.get_BoundingBox(null);
                if (bbox != null)
                {
                    double offset = 200.0 / 304.8; // 200mm offset
                    double midX = (bbox.Min.X + bbox.Max.X) / 2.0;
                    double midY = (bbox.Min.Y + bbox.Max.Y) / 2.0;
                    // 5 evenly spaced points from bottom to top of run
                    for (int i = 0; i <= 4; i++)
                    {
                        double z = bbox.Min.Z + ((double)i / 4.0) * (bbox.Max.Z - bbox.Min.Z) + offset;
                        points.Add(new XYZ(midX, midY, z));
                    }
                }
            }

            return points;
        }

        /// <summary>
        /// Check 6: Handrail height (min 1.0m / 100cm from nosing).
        /// Multi-strategy extraction for different railing families.
        /// </summary>
        private ComplianceResult CheckHandrailHeight(Stairs stair, string stairName)
        {
            double requiredM = 1.00; // 1000mm = 100cm

            var railingIds = stair.GetAssociatedRailings();

            if (railingIds == null || railingIds.Count == 0)
            {
                return new ComplianceResult
                {
                    Parameter = $"{stairName}: Handrail Height",
                    Required = $">= {requiredM:F2}m (100cm)",
                    Actual = "No railings attached",
                    Status = "WARNING",
                    Details = "No railing elements associated with this stair. " +
                              "Verify min 100cm handrail from nosing manually — Reg 37(13)"
                };
            }

            double bestHeightFt = 0;
            bool foundHeight = false;
            string extractionNote = "";

            foreach (var railId in railingIds)
            {
                var railing = _doc.GetElement(railId);
                if (railing == null) continue;

                // Strategy 1: Try BuiltInParameter for railing height
                double ht = TryGetRailingHeightFromBuiltIn(railing);
                if (ht > 0)
                {
                    if (!foundHeight || ht < bestHeightFt) bestHeightFt = ht;
                    foundHeight = true;
                    extractionNote = "BuiltInParameter";
                    continue;
                }

                // Strategy 2: Search instance parameters for "Height" (excluding irrelevant ones)
                ht = TryGetRailingHeightFromParams(railing);
                if (ht > 0)
                {
                    if (!foundHeight || ht < bestHeightFt) bestHeightFt = ht;
                    foundHeight = true;
                    extractionNote = "instance parameter";
                    continue;
                }

                // Strategy 3: Search TYPE parameters (where "Top Rail > Height" lives)
                var railType = _doc.GetElement(railing.GetTypeId());
                if (railType != null)
                {
                    ht = TryGetRailingHeightFromParams(railType);
                    if (ht > 0)
                    {
                        if (!foundHeight || ht < bestHeightFt) bestHeightFt = ht;
                        foundHeight = true;
                        extractionNote = "type parameter";
                        continue;
                    }

                    // Strategy 4: Top Rail Type indirect lookup
                    // In Revit, the railing type has a "Top Rail Type" parameter (ElementId)
                    // that references a separate type element with its own "Height" property.
                    // This is where "Top Rail > Height: 796.9" lives in the type properties dialog.
                    if (!foundHeight)
                    {
                        foreach (Parameter p in railType.Parameters)
                        {
                            if (p.Definition == null) continue;
                            string pn = p.Definition.Name;
                            if (pn.IndexOf("Top Rail", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                pn.IndexOf("Type", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                p.StorageType == StorageType.ElementId)
                            {
                                ElementId topRailTypeId = p.AsElementId();
                                if (topRailTypeId != null &&
                                    topRailTypeId != ElementId.InvalidElementId)
                                {
                                    Element topRailType = _doc.GetElement(topRailTypeId);
                                    if (topRailType != null)
                                    {
                                        ht = TryGetRailingHeightFromParams(topRailType);
                                        if (ht > 0)
                                        {
                                            if (!foundHeight || ht < bestHeightFt) bestHeightFt = ht;
                                            foundHeight = true;
                                            extractionNote = "Top Rail Type → Height";
                                        }
                                    }
                                }
                                break;
                            }
                        }
                    }

                    // Strategy 5: Iterate ALL ordered parameters on type for "Height"
                    foreach (Parameter p in railType.GetOrderedParameters())
                    {
                        if (p.Definition == null) continue;
                        string pName = p.Definition.Name;
                        bool isHeight = pName.Equals("Height", StringComparison.OrdinalIgnoreCase) ||
                                        pName.IndexOf("Rail", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                        pName.IndexOf("Height", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!isHeight) continue;
                        if (p.StorageType != StorageType.Double || !p.HasValue) continue;

                        double val = p.AsDouble();
                        // Sanity check: if value > 10ft (~3m), it's probably stored in mm
                        if (val > 10.0)
                            val = val / 304.8; // mm to feet
                        if (val > 0.5 && val < 6.56) // between ~150mm and 2m
                        {
                            if (!foundHeight || val < bestHeightFt) bestHeightFt = val;
                            foundHeight = true;
                            extractionNote = "type ordered parameter '" + pName + "'";
                        }
                    }
                }

                // Strategy 6: Search instance ordered parameters
                if (!foundHeight)
                {
                    foreach (Parameter p in railing.GetOrderedParameters())
                    {
                        if (p.Definition == null) continue;
                        string pName = p.Definition.Name;
                        bool isHeight = pName.Equals("Height", StringComparison.OrdinalIgnoreCase) ||
                                        (pName.IndexOf("Rail", StringComparison.OrdinalIgnoreCase) >= 0 &&
                                         pName.IndexOf("Height", StringComparison.OrdinalIgnoreCase) >= 0) ||
                                        pName.IndexOf("Top Rail", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (!isHeight) continue;
                        if (p.StorageType != StorageType.Double || !p.HasValue) continue;

                        double val = p.AsDouble();
                        if (val > 10.0)
                            val = val / 304.8;
                        if (val > 0.5 && val < 6.56)
                        {
                            if (!foundHeight || val < bestHeightFt) bestHeightFt = val;
                            foundHeight = true;
                            extractionNote = "instance ordered parameter '" + pName + "'";
                        }
                    }
                }
            }

            if (foundHeight && bestHeightFt > 0)
            {
                double actualM = bestHeightFt * FEET_TO_METERS;
                bool pass = actualM >= (requiredM - 0.01);

                return new ComplianceResult
                {
                    Parameter = $"{stairName}: Handrail Height",
                    Required = $">= {requiredM:F2}m (100cm)",
                    Actual = $"{actualM:F2}m ({actualM * 100:F0}cm)",
                    Status = pass ? "PASS" : "FAIL",
                    Details = (pass ? "Compliant" : "VIOLATION") +
                              $" — Min 100cm handrail height from nosing — Reg 37(13)" +
                              $" [read via {extractionNote}]"
                };
            }

            // No height found
            return new ComplianceResult
            {
                Parameter = $"{stairName}: Handrail Height",
                Required = $">= {requiredM:F2}m (100cm)",
                Actual = "Height not readable",
                Status = "WARNING",
                Details = "Could not read handrail height from " + railingIds.Count +
                          " associated railing(s). " +
                          "Verify min 100cm handrail from nosing manually — Reg 37(13)"
            };
        }

        /// <summary>
        /// Try reading railing height from well-known built-in parameter names.
        /// Returns height in feet, or 0 if not found.
        /// </summary>
        private double TryGetRailingHeightFromBuiltIn(Element railing)
        {
            // Try common parameter names that Revit system railings use
            string[] knownNames = new[] { "Railing Height", "Top Rail Height", "Rail Height" };

            foreach (string name in knownNames)
            {
                foreach (Parameter p in railing.Parameters)
                {
                    if (p.Definition != null &&
                        p.Definition.Name.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                        p.StorageType == StorageType.Double && p.HasValue)
                    {
                        double val = p.AsDouble();
                        if (val > 10.0) val = val / 304.8; // mm to feet
                        if (val > 0.5 && val < 6.56)
                            return val;
                    }
                }
            }
            return 0;
        }

        /// <summary>
        /// Search element parameters for handrail height.
        /// Excludes irrelevant "height" parameters (Unconnected Height, etc.)
        /// Returns height in feet, or 0 if not found.
        /// </summary>
        private double TryGetRailingHeightFromParams(Element elem)
        {
            foreach (Parameter p in elem.Parameters)
            {
                if (p.Definition == null) continue;
                string pName = p.Definition.Name;
                string pNameUpper = pName.ToUpperInvariant();

                // Must contain "HEIGHT"
                if (!pNameUpper.Contains("HEIGHT")) continue;

                // Exclude clearly irrelevant parameters
                if (pNameUpper.Contains("UNCONNECTED") ||
                    pNameUpper.Contains("BASE") ||
                    pNameUpper.Contains("SILL") ||
                    pNameUpper.Contains("OFFSET"))
                    continue;

                if (p.StorageType != StorageType.Double || !p.HasValue)
                    continue;

                double val = p.AsDouble();
                // Sanity: if > 10ft, assume stored in mm
                if (val > 10.0)
                    val = val / 304.8;
                if (val > 0.5 && val < 6.56) // 150mm to 2m plausible range
                    return val;
            }
            return 0;
        }

        /// <summary>
        /// Check 7: Landing width (must be >= staircase width).
        /// Reads from STAIRS_ATTR_LANDING_WIDTH or estimates from geometry.
        /// </summary>
        private ComplianceResult CheckLandingWidth(Stairs stair, string stairName,
            double buildingHeightM, string occupancyType)
        {
            double requiredWidthM = GetRequiredWidth(buildingHeightM, occupancyType);

            // Check for landing elements within the stair
            // Revit 2024: StairsLanding elements are part of stair runs
            var landingIds = stair.GetStairsLandings();
            if (landingIds != null && landingIds.Count > 0)
            {
                double minLandingWidthFt = double.MaxValue;
                bool foundWidth = false;

                foreach (var landingId in landingIds)
                {
                    var landing = _doc.GetElement(landingId) as StairsLanding;
                    if (landing == null) continue;

                    // Try to get width from landing's bounding box
                    var bbox = landing.get_BoundingBox(null);
                    if (bbox != null)
                    {
                        double dx = bbox.Max.X - bbox.Min.X;
                        double dy = bbox.Max.Y - bbox.Min.Y;
                        // Landing width is typically the smaller horizontal dimension
                        double landingW = Math.Min(dx, dy);
                        if (landingW > 0 && landingW < minLandingWidthFt)
                        {
                            minLandingWidthFt = landingW;
                            foundWidth = true;
                        }
                    }
                }

                if (foundWidth && minLandingWidthFt < double.MaxValue)
                {
                    double actualM = minLandingWidthFt * FEET_TO_METERS;
                    bool pass = actualM >= (requiredWidthM - 0.01);

                    return new ComplianceResult
                    {
                        
                        Parameter = $"{stairName}: Landing Width",
                        Required = $">= {requiredWidthM:F2}m (= stair width)",
                        Actual = $"{actualM:F2}m",
                        Status = pass ? "PASS" : "FAIL",
                        Details = (pass ? "Compliant" : "VIOLATION") +
                                  " — Landing width must be >= staircase width — Reg 37(13)"
                    };
                }
            }

            // No landing data — could be a straight-run stair with no mid-landing
            if (landingIds == null || landingIds.Count == 0)
            {
                return null; // No landings = straight run, no landing check needed
            }

            return new ComplianceResult
            {
                
                Parameter = $"{stairName}: Landing Width",
                Required = $">= {requiredWidthM:F2}m (= stair width)",
                Actual = "Not measurable",
                Status = "WARNING",
                Details = "Landing dimensions could not be extracted. " +
                          "Verify landing width >= stair width manually — Reg 37(13)"
            };
        }

        // ════════════════════════════════════════════════════════════════════
        // HELPER METHODS
        // ════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Gets the required minimum staircase width in meters based on
        /// building height and occupancy type.
        /// </summary>
        private double GetRequiredWidth(double buildingHeightM, string occupancyType)
        {
            string occ = (occupancyType ?? "Residential").ToLowerInvariant();

            if (occ.Contains("assembly") || occ.Contains("auditorium") || occ.Contains("theatre"))
                return 2.00;

            if (occ.Contains("residential"))
            {
                // Residential: 1.0m for ≤24m, 1.5m for high-rise
                return buildingHeightM > 24.0 ? 1.50 : 1.00;
            }

            // Commercial, educational, institutional, industrial, etc.
            return 1.50;
        }

        /// <summary>
        /// Returns a human-readable rule explanation for width requirement.
        /// </summary>
        private string GetWidthRuleExplanation(double buildingHeightM, string occupancyType)
        {
            string occ = (occupancyType ?? "Residential").ToLowerInvariant();

            if (occ.Contains("assembly"))
                return "Assembly buildings: min 2.0m — Reg 37(13), Table 16";

            if (occ.Contains("residential"))
            {
                if (buildingHeightM > 24.0)
                    return "Residential high-rise (>24m): min 1.5m — Reg 37(13), Table 16";
                return "Residential (≤24m): min 1.0m — Reg 37(13), Table 16";
            }

            return $"{occupancyType}: min 1.5m — Reg 37(13), Table 16";
        }

        /// <summary>
        /// Gets the overall building height in meters from the model.
        /// Uses the highest level elevation or the tallest element's bounding box.
        /// </summary>
        private double GetBuildingHeight()
        {
            // Method 1: Use levels — find highest level elevation
            double maxElevationFt = 0;

            var levels = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Levels)
                .WhereElementIsNotElementType()
                .Cast<Level>()
                .ToList();

            if (levels.Count > 0)
            {
                maxElevationFt = levels.Max(l => l.Elevation);
            }

            // Method 2: Check wall heights for a better estimate
            var walls = new FilteredElementCollector(_doc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .WhereElementIsNotElementType()
                .ToList();

            foreach (var wallElem in walls)
            {
                var bbox = wallElem.get_BoundingBox(null);
                if (bbox != null && bbox.Max.Z > maxElevationFt)
                {
                    maxElevationFt = bbox.Max.Z;
                }
            }

            return maxElevationFt * FEET_TO_METERS;
        }

        /// <summary>
        /// Gets a display name for the stair element.
        /// Uses the stair's Mark parameter or Name, falling back to "Stair N".
        /// </summary>
        private string GetStairName(Stairs stair, int index)
        {
            // Try Mark parameter first
            var markParam = stair.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);
            if (markParam != null && markParam.HasValue && !string.IsNullOrWhiteSpace(markParam.AsString()))
            {
                return $"Stair '{markParam.AsString()}'";
            }

            // Try stair name/comments
            var nameParam = stair.get_Parameter(BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
            if (nameParam != null && nameParam.HasValue && !string.IsNullOrWhiteSpace(nameParam.AsString()))
            {
                return $"Stair '{nameParam.AsString()}'";
            }

            // Fall back to index
            return $"Stair {index}";
        }
    }
}
