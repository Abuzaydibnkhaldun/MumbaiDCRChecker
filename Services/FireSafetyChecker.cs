using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using MumbaiDCRChecker.Models;

namespace MumbaiDCRChecker.Services
{
    /// <summary>
    /// Phase 4: Fire Safety Compliance Checker
    /// DCPR 2034 Regulation 47 (Fire Protection Requirements) + Regulation 48 (Requirements of Exits)
    /// NBC 2016 Part 4 (Fire and Life Safety)
    /// 
    /// 6 Tier-1 Checks (fully automatable):
    ///   1. Travel distance to nearest exit (Reg 47(3)(ii))
    ///   2. Dead-end corridor length (Reg 47(3) Note, NBC 4.4.2.2(c))
    ///   3. Number of fire staircases (Reg 47(5))
    ///   4. Fire stair width - extended (Reg 47(5), Table 22)
    ///   5. Refuge area (Reg 47(8))
    ///   6. Fire door detection (Reg 47(3), 48(2), 48(4)(d))
    /// 
    /// Tier-3 Warnings (manual verification items)
    /// </summary>
    public static class FireSafetyChecker
    {
        private const double FEET_TO_METERS = 0.3048;
        private const double METERS_TO_FEET = 1.0 / 0.3048;
        private const string CATEGORY = "Fire Safety";

        // Minimum vertical span (meters) for a staircase to be considered a "main" staircase.
        // Stairs shorter than this (e.g., ground-to-plinth, split-level steps) are excluded
        // from fire safety and staircase compliance checks.
        // Typical story height: 2.7-3.0m. Using 2.4m as safe threshold.
        private const double MIN_STORY_HEIGHT_M = 2.4;
        private const double MIN_STORY_HEIGHT_FT = MIN_STORY_HEIGHT_M / FEET_TO_METERS; // ~7.87 ft

        // ============================================================
        //  TRAVEL DISTANCE LIMITS (meters) — DCPR 2034 Reg 47(3)(ii)
        // ============================================================
        // Without sprinklers:
        //   Residential, Educational, Institutional: 22.5m
        //   Assembly, Commercial, Business, Industrial, Hazardous, Storage: 30m
        // With sprinklers: +50% (per DCPR 2034 Reg 47(3)(ii) note)
        // Dead-end corridor: max half the travel distance (DCPR 2034 Reg 47(3) Note)

        private static double GetTravelDistanceLimit(string occupancy, bool hasSprinklers)
        {
            double baseLimit;
            string occUpper = (occupancy ?? "Residential").ToUpperInvariant();

            if (occUpper.Contains("RESIDENTIAL") || occUpper.Contains("EDUCATIONAL") ||
                occUpper.Contains("INSTITUTIONAL"))
            {
                baseLimit = 22.5; // meters
            }
            else
            {
                baseLimit = 30.0; // meters — Assembly, Commercial, Business, etc.
            }

            if (hasSprinklers)
            {
                baseLimit *= 1.5; // +50% with sprinklers in passages and corridors
            }

            return baseLimit;
        }

        private static double GetDeadEndLimit(string occupancy)
        {
            string occUpper = (occupancy ?? "Residential").ToUpperInvariant();

            // DCPR 2034 Reg 47(3) Note + NBC 2016 Clause 4.4.2.2(c):
            // Educational, Institutional, Assembly: 6m
            // Others: 15m
            if (occUpper.Contains("EDUCATIONAL") || occUpper.Contains("INSTITUTIONAL") ||
                occUpper.Contains("ASSEMBLY"))
            {
                return 6.0;
            }
            return 15.0;
        }

        // ============================================================
        //  FIRE STAIRCASE WIDTH LIMITS (meters) — DCPR 2034 Reg 47(5) + Table 22
        // ============================================================

        private static double GetFireStairMinWidth(string occupancy, double buildingHeightM)
        {
            string occUpper = (occupancy ?? "Residential").ToUpperInvariant();

            // Reg 47(5)(b): >70m → 2.0m each enclosed staircase
            if (buildingHeightM > 70.0)
            {
                return 2.0;
            }

            // Reg 47(5)(A) Note: enclosed type min 1.5m
            if (buildingHeightM > 32.0)
            {
                return 1.5;
            }

            // Table 22 base widths by occupancy
            if (occUpper.Contains("RESIDENTIAL"))
            {
                // Row-housing/Duplex: 0.9m, Hotels: 1.5m, General residential: 1.2m
                if (occUpper.Contains("HOTEL"))
                    return 1.5;
                if (occUpper.Contains("DUPLEX") || occUpper.Contains("ROW"))
                    return 0.9;
                return 1.2;
            }
            if (occUpper.Contains("EDUCATIONAL"))
            {
                return buildingHeightM > 32.0 ? 2.0 : 1.5;
            }
            if (occUpper.Contains("INSTITUTIONAL") || occUpper.Contains("HOSPITAL"))
            {
                return 2.0;
            }
            if (occUpper.Contains("ASSEMBLY"))
            {
                return 2.0;
            }
            // Mercantile, Business, Industrial, Storage, Hazardous
            return 1.5;
        }

        // ============================================================
        //  REQUIRED STAIRCASE COUNT — DCPR 2034 Reg 47(5)
        // ============================================================

        private static int GetRequiredStaircaseCount(string occupancy, double buildingHeightM,
            double maxFloorAreaSqM, double travelDistanceLimitM)
        {
            string occUpper = (occupancy ?? "Residential").ToUpperInvariant();

            // Reg 47(5)(B): Institutional/Special/Non-residential high-rise: min 2
            bool isInstitutionalOrSpecial = occUpper.Contains("INSTITUTIONAL") ||
                occUpper.Contains("HOSPITAL") || occUpper.Contains("ASSEMBLY");

            if (isInstitutionalOrSpecial && buildingHeightM > 24.0)
            {
                return 2;
            }

            // Reg 47(5)(b): >70m → 2 enclosed type staircases
            if (buildingHeightM > 70.0)
            {
                return 2;
            }

            // Reg 47(5)(A): High-rise residential ≤70m — additional staircase needed unless:
            //   (i) Travel distance not exceeded AND (ii) Floor area ≤ 500 sqm
            // So if floor > 500sqm → need additional (2 total)
            if (buildingHeightM > 24.0)
            {
                if (maxFloorAreaSqM > 500.0)
                {
                    return 2;
                }
                // If travel distance is within limits and floor ≤ 500sqm, 1 may suffice
                // But high-rise/special always needs at least 2 per NBC 2016
                if (buildingHeightM > 32.0)
                {
                    return 2;
                }
            }

            // ≤24m: 1 staircase minimum (basic requirement)
            return 1;
        }

        // ============================================================
        //  PUBLIC API — Run all fire safety checks
        // ============================================================

        /// <summary>
        /// Runs all Phase 4 fire safety compliance checks.
        /// Detects building blocks and runs checks PER BLOCK.
        /// Only staircases with "Fire" in Comments are treated as fire staircases.
        /// </summary>
        public static List<ComplianceResult> CheckAll(Document doc, DCRParameters parameters)
        {
            var results = new List<ComplianceResult>();

            // Gather shared data
            double buildingHeightM = GetBuildingHeightMeters(doc, parameters);
            string occupancy = parameters.Occupancy ?? "Residential";
            bool hasSprinklers = parameters.HasSprinklers;

            // Get ALL rooms grouped by level
            Dictionary<ElementId, List<Room>> allRoomsByLevel = GetRoomsByLevel(doc);

            // Get ALL staircase groups (already filtered for min story height)
            List<StaircaseGroup> allStaircaseGroups = GetStaircaseGroups(doc);

            // Filter for FIRE staircases only (Comments contains "Fire")
            List<StaircaseGroup> fireStaircaseGroups = FilterFireStaircases(doc, allStaircaseGroups);

            // Get all fire-rated doors
            List<FamilyInstance> allFireRatedDoors = GetFireRatedDoors(doc);

            // Detect building blocks from wall spatial clustering (no room dependency)
            List<BuildingBlock> blocks = DetectBuildingBlocks(doc, allRoomsByLevel);
            bool multiBlock = blocks.Count > 1;

            // Report if no fire staircases are marked
            if (fireStaircaseGroups.Count == 0 && allStaircaseGroups.Count > 0)
            {
                results.Add(new ComplianceResult
                {
                    Category = CATEGORY,
                    Parameter = "Fire Staircase Detection",
                    Required = "Comments = 'Fire Staircase'",
                    Actual = allStaircaseGroups.Count + " staircase group(s), 0 marked as fire",
                    Status = "WARNING",
                    Details = "No staircases have 'Fire' in Comments parameter (Element Identity). " +
                              "Mark fire staircases by setting Comments to 'Fire Staircase'. " +
                              allStaircaseGroups.Count + " staircase group(s) found but excluded from fire safety checks.",
                    ViolatingElementIds = new ElementId[0]
                });
            }
            else if (fireStaircaseGroups.Count > 0)
            {
                int unmarked = allStaircaseGroups.Count - fireStaircaseGroups.Count;
                results.Add(new ComplianceResult
                {
                    Category = CATEGORY,
                    Parameter = "Fire Staircase Detection",
                    Required = "Comments = 'Fire Staircase'",
                    Actual = fireStaircaseGroups.Count + " fire staircase(s) identified",
                    Status = "PASS",
                    Details = fireStaircaseGroups.Count + " of " + allStaircaseGroups.Count +
                              " staircase group(s) marked as fire staircases via Comments parameter." +
                              (unmarked > 0 ? " " + unmarked + " unmarked group(s) excluded from fire safety checks." : ""),
                    ViolatingElementIds = new ElementId[0]
                });
            }

            // Report building block detection
            if (multiBlock)
            {
                string blockInfo = "";
                foreach (BuildingBlock b in blocks)
                {
                    blockInfo += b.Name + " (" + b.RoomIds.Count + " rooms), ";
                }
                blockInfo = blockInfo.TrimEnd(',', ' ');

                results.Add(new ComplianceResult
                {
                    Category = CATEGORY,
                    Parameter = "Building Blocks Detected",
                    Required = "Per-block analysis",
                    Actual = blocks.Count + " blocks found",
                    Status = "PASS",
                    Details = "Multiple building blocks detected via wall clustering. " +
                              "Fire safety checks run independently per block. " +
                              blockInfo,
                    ViolatingElementIds = new ElementId[0]
                });
            }

            // ===== Run checks PER BLOCK =====
            foreach (BuildingBlock block in blocks)
            {
                // Filter data to this block
                Dictionary<ElementId, List<Room>> blockRooms = FilterRoomsToBlock(allRoomsByLevel, block);
                List<StaircaseGroup> blockFireStairs = FilterStaircasesToBlock(fireStaircaseGroups, block);
                List<FamilyInstance> blockFireDoors = FilterDoorsToBlock(allFireRatedDoors, block);
                Dictionary<ElementId, List<XYZ>> blockExitPoints =
                    GetExitPointsByLevel(doc, blockFireStairs, blockFireDoors);

                // CHECK 1: Travel Distance (per block)
                var travelResults = CheckTravelDistance(doc, parameters, blockRooms,
                    blockExitPoints, buildingHeightM, occupancy, hasSprinklers);
                PrefixBlockName(travelResults, block.Name);
                results.AddRange(travelResults);

                // CHECK 2: Dead-End Corridor (per block)
                var deadEndResults = CheckDeadEndCorridor(doc, parameters, blockRooms,
                    buildingHeightM, occupancy);
                PrefixBlockName(deadEndResults, block.Name);
                results.AddRange(deadEndResults);

                // CHECK 3: Number of Fire Staircases (per block)
                var countResults = CheckFireStaircaseCount(doc, parameters, blockFireStairs,
                    blockRooms, buildingHeightM, occupancy, hasSprinklers);
                PrefixBlockName(countResults, block.Name);
                results.AddRange(countResults);

                // CHECK 4: Fire Stair Width (per block)
                var widthResults = CheckFireStairWidth(doc, parameters, blockFireStairs,
                    buildingHeightM, occupancy);
                PrefixBlockName(widthResults, block.Name);
                results.AddRange(widthResults);

                // CHECK 5: Refuge Area (per block)
                var refugeResults = CheckRefugeArea(doc, parameters, blockRooms, buildingHeightM);
                PrefixBlockName(refugeResults, block.Name);
                results.AddRange(refugeResults);

                // CHECK 6: Fire Doors (per block)
                var doorResults = CheckFireDoors(doc, parameters, blockFireStairs,
                    blockFireDoors, buildingHeightM);
                PrefixBlockName(doorResults, block.Name);
                results.AddRange(doorResults);
            }

            // TIER 3: Warnings (global, not per block)
            results.AddRange(GetTier3Warnings(buildingHeightM));

            return results;
        }

        // ============================================================
        //  CHECK 1: TRAVEL DISTANCE — Reg 47(3)(ii)
        // ============================================================

        private static List<ComplianceResult> CheckTravelDistance(
            Document doc, DCRParameters parameters,
            Dictionary<ElementId, List<Room>> roomsByLevel,
            Dictionary<ElementId, List<XYZ>> exitPointsByLevel,
            double buildingHeightM, string occupancy, bool hasSprinklers)
        {
            var results = new List<ComplianceResult>();
            double limitM = GetTravelDistanceLimit(occupancy, hasSprinklers);
            double limitFt = limitM * METERS_TO_FEET;

            // Track worst overall
            double worstDistanceM = 0;
            string worstRoomName = "";
            string worstLevelName = "";
            ElementId worstRoomId = ElementId.InvalidElementId;

            int levelsWithNoRooms = 0;
            int totalLevels = 0;
            int totalRoomsChecked = 0;
            int roomsOutsideCoverage = 0; // rooms with ANY point beyond limit (circle concept)
            var failingRoomIds = new List<ElementId>();

            // Collect ALL staircase exit points (XY only) for circle-coverage analysis
            var allExitPointsXY = new List<XYZ>();
            foreach (var epList in exitPointsByLevel.Values)
            {
                foreach (XYZ ep in epList)
                {
                    allExitPointsXY.Add(new XYZ(ep.X, ep.Y, 0));
                }
            }

            // Check each level
            foreach (var kvp in roomsByLevel)
            {
                ElementId levelId = kvp.Key;
                List<Room> rooms = kvp.Value;
                totalLevels++;

                if (rooms.Count == 0)
                {
                    levelsWithNoRooms++;
                    continue;
                }

                // Get exit points for this level
                List<XYZ> exitPoints;
                if (!exitPointsByLevel.TryGetValue(levelId, out exitPoints) || exitPoints.Count == 0)
                {
                    // No exits found on this level — use staircase XY positions
                    // (stairs span multiple levels, their base might be on a different level)
                    exitPoints = new List<XYZ>(allExitPointsXY);
                }

                if (exitPoints.Count == 0)
                {
                    // No exits found at all — FAIL
                    string levelName = GetLevelName(doc, levelId);
                    results.Add(new ComplianceResult
                    {
                        Category = CATEGORY,
                        Parameter = "Travel Distance (" + levelName + ")",
                        Required = "≤ " + limitM.ToString("F1") + "m",
                        Actual = "No exits found",
                        Status = "FAIL",
                        Details = "No fire staircases or fire-rated exit doors detected on or near " + levelName +
                                  ". Reg 47(3)(ii)",
                        ViolatingElementIds = new ElementId[0]
                    });
                    continue;
                }

                // For each room, find max distance from ANY room point to nearest exit
                // This is the "circle coverage" concept:
                //   A room FAILS if any of its boundary points is beyond the DCR radius
                //   from ALL exit points (i.e., outside every exit's coverage circle).
                foreach (Room room in rooms)
                {
                    if (room == null || room.Area < 1.0) // Skip tiny/invalid rooms
                        continue;

                    totalRoomsChecked++;
                    List<XYZ> testPoints = GetRoomTestPoints(room);
                    if (testPoints.Count == 0)
                        continue;

                    double roomWorstDist = 0;
                    foreach (XYZ pt in testPoints)
                    {
                        // Find distance to nearest exit (stair or fire door)
                        double nearestExitDist = double.MaxValue;
                        foreach (XYZ exitPt in exitPoints)
                        {
                            // Horizontal XY distance only (same floor plane)
                            double dx = pt.X - exitPt.X;
                            double dy = pt.Y - exitPt.Y;
                            double dist = Math.Sqrt(dx * dx + dy * dy);
                            if (dist < nearestExitDist)
                                nearestExitDist = dist;
                        }
                        if (nearestExitDist > roomWorstDist)
                            roomWorstDist = nearestExitDist;
                    }

                    double roomWorstDistM = roomWorstDist * FEET_TO_METERS;

                    // Track rooms outside coverage circle
                    if (roomWorstDistM > limitM)
                    {
                        roomsOutsideCoverage++;
                        failingRoomIds.Add(room.Id);
                    }

                    if (roomWorstDistM > worstDistanceM)
                    {
                        worstDistanceM = roomWorstDistM;
                        worstRoomName = room.get_Parameter(BuiltInParameter.ROOM_NAME) != null
                            ? room.get_Parameter(BuiltInParameter.ROOM_NAME).AsString() ?? "Unnamed"
                            : "Unnamed";
                        worstLevelName = GetLevelName(doc, levelId);
                        worstRoomId = room.Id;
                    }
                }
            }

            // Report travel distance result
            string status = worstDistanceM <= limitM ? "PASS" : "FAIL";
            string sprinklerNote = hasSprinklers ? " (sprinklered +50%)" : "";

            // Build detailed description
            string details = "Checked " + totalRoomsChecked + " rooms across " +
                             (totalLevels - levelsWithNoRooms) + " floors. " +
                             "Measurement: straight-line XY distance from room boundary points " +
                             "to nearest fire staircase or fire-rated door. " +
                             "Reg 47(3)(ii).";

            if (status == "FAIL")
            {
                details += " Worst: " + worstRoomName + " on " + worstLevelName +
                           " at " + worstDistanceM.ToString("F1") + "m " +
                           "(exceeds by " + (worstDistanceM - limitM).ToString("F1") + "m). " +
                           roomsOutsideCoverage + " room(s) outside coverage radius.";
            }
            else
            {
                details += " Worst: " + worstRoomName + " on " + worstLevelName +
                           " at " + worstDistanceM.ToString("F1") + "m. " +
                           "All rooms within " + limitM.ToString("F1") + "m coverage.";
            }

            results.Add(new ComplianceResult
            {
                Category = CATEGORY,
                Parameter = "Travel Distance (worst)",
                Required = "≤ " + limitM.ToString("F1") + "m" + sprinklerNote,
                Actual = worstDistanceM.ToString("F1") + "m",
                Status = status,
                Details = details,
                ViolatingElementIds = status == "FAIL"
                    ? failingRoomIds.ToArray()
                    : new ElementId[0]
            });

            // Warn about levels with no rooms
            if (levelsWithNoRooms > 0)
            {
                results.Add(new ComplianceResult
                {
                    Category = CATEGORY,
                    Parameter = "Travel Distance (coverage)",
                    Required = "Rooms on all floors",
                    Actual = levelsWithNoRooms + " of " + totalLevels + " floors missing Rooms",
                    Status = "WARNING",
                    Details = "Place Rooms on all floors for complete travel distance analysis. " +
                              "Floors without Rooms cannot be checked.",
                    ViolatingElementIds = new ElementId[0]
                });
            }

            return results;
        }

        // ============================================================
        //  CHECK 2: DEAD-END CORRIDOR — Reg 47(3) Note, NBC 4.4.2.2(c)
        // ============================================================

        private static List<ComplianceResult> CheckDeadEndCorridor(
            Document doc, DCRParameters parameters,
            Dictionary<ElementId, List<Room>> roomsByLevel,
            double buildingHeightM, string occupancy)
        {
            var results = new List<ComplianceResult>();
            double limitM = GetDeadEndLimit(occupancy);

            double worstDeadEndM = 0;
            string worstCorridorName = "";
            string worstLevelName = "";
            ElementId worstCorridorId = ElementId.InvalidElementId;
            int deadEndCount = 0;

            // Get the last phase for FromRoom/ToRoom queries
            Phase lastPhase = GetLastPhase(doc);
            if (lastPhase == null)
            {
                results.Add(new ComplianceResult
                {
                    Category = CATEGORY,
                    Parameter = "Dead-End Corridor",
                    Required = "≤ " + limitM.ToString("F1") + "m",
                    Actual = "N/A",
                    Status = "WARNING",
                    Details = "No phases found in document. Cannot evaluate door-room connectivity.",
                    ViolatingElementIds = new ElementId[0]
                });
                return results;
            }

            foreach (var kvp in roomsByLevel)
            {
                ElementId levelId = kvp.Key;
                List<Room> rooms = kvp.Value;

                // Find corridor/passage rooms
                foreach (Room room in rooms)
                {
                    if (room == null || room.Area < 1.0)
                        continue;

                    string roomName = room.get_Parameter(BuiltInParameter.ROOM_NAME) != null
                        ? (room.get_Parameter(BuiltInParameter.ROOM_NAME).AsString() ?? "")
                        : "";

                    string nameUpper = roomName.ToUpperInvariant();
                    bool isCorridor = nameUpper.Contains("CORRIDOR") ||
                                      nameUpper.Contains("PASSAGE") ||
                                      nameUpper.Contains("LOBBY") ||
                                      nameUpper.Contains("HALLWAY");

                    if (!isCorridor)
                        continue;

                    // Count doors connecting to this corridor room
                    int doorCount = CountDoorsInRoom(doc, room, lastPhase);

                    // A dead-end corridor has only 1 door/opening connection
                    if (doorCount <= 1)
                    {
                        deadEndCount++;

                        // Measure corridor length (longest BB dimension in XY)
                        BoundingBoxXYZ bb = room.get_BoundingBox(null);
                        if (bb != null)
                        {
                            double lengthFt = Math.Max(
                                bb.Max.X - bb.Min.X,
                                bb.Max.Y - bb.Min.Y);
                            double lengthM = lengthFt * FEET_TO_METERS;

                            if (lengthM > worstDeadEndM)
                            {
                                worstDeadEndM = lengthM;
                                worstCorridorName = roomName;
                                worstLevelName = GetLevelName(doc, levelId);
                                worstCorridorId = room.Id;
                            }
                        }
                    }
                }
            }

            if (deadEndCount == 0)
            {
                results.Add(new ComplianceResult
                {
                    Category = CATEGORY,
                    Parameter = "Dead-End Corridor",
                    Required = "≤ " + limitM.ToString("F1") + "m",
                    Actual = "None detected",
                    Status = "PASS",
                    Details = "No dead-end corridors found (corridors with single door connection). " +
                              "Reg 47(3) Note.",
                    ViolatingElementIds = new ElementId[0]
                });
            }
            else
            {
                string status = worstDeadEndM <= limitM ? "PASS" : "FAIL";
                results.Add(new ComplianceResult
                {
                    Category = CATEGORY,
                    Parameter = "Dead-End Corridor (worst)",
                    Required = "≤ " + limitM.ToString("F1") + "m",
                    Actual = worstDeadEndM.ToString("F1") + "m (" + deadEndCount + " found)",
                    Status = status,
                    Details = "Worst: \"" + worstCorridorName + "\" on " + worstLevelName +
                              ". " + deadEndCount + " dead-end corridor(s) total. " +
                              "Reg 47(3) Note / NBC 4.4.2.2(c).",
                    ViolatingElementIds = status == "FAIL" && worstCorridorId != ElementId.InvalidElementId
                        ? new ElementId[] { worstCorridorId }
                        : new ElementId[0]
                });
            }

            return results;
        }

        // ============================================================
        //  CHECK 3: NUMBER OF FIRE STAIRCASES — Reg 47(5)
        // ============================================================

        private static List<ComplianceResult> CheckFireStaircaseCount(
            Document doc, DCRParameters parameters,
            List<StaircaseGroup> staircaseGroups,
            Dictionary<ElementId, List<Room>> roomsByLevel,
            double buildingHeightM, string occupancy, bool hasSprinklers)
        {
            var results = new List<ComplianceResult>();

            // Find max floor area across all levels (for this block)
            double maxFloorAreaSqM = 0;
            string maxFloorAreaLevel = "";
            foreach (var kvp in roomsByLevel)
            {
                double levelArea = 0;
                foreach (Room r in kvp.Value)
                {
                    if (r != null && r.Area > 0)
                        levelArea += r.Area * FEET_TO_METERS * FEET_TO_METERS;
                }
                if (levelArea > maxFloorAreaSqM)
                {
                    maxFloorAreaSqM = levelArea;
                    maxFloorAreaLevel = GetLevelName(doc, kvp.Key);
                }
            }

            double travelLimit = GetTravelDistanceLimit(occupancy, hasSprinklers);
            int requiredCount = GetRequiredStaircaseCount(occupancy, buildingHeightM,
                maxFloorAreaSqM, travelLimit);
            int actualCount = staircaseGroups.Count;

            string status = actualCount >= requiredCount ? "PASS" : "FAIL";

            string heightTier;
            if (buildingHeightM <= 24.0) heightTier = "≤24m";
            else if (buildingHeightM <= 32.0) heightTier = "24-32m";
            else if (buildingHeightM <= 70.0) heightTier = "32-70m";
            else heightTier = ">70m";

            string details = "Building height: " + buildingHeightM.ToString("F1") + "m (" + heightTier + "). " +
                             "Max floor area: " + maxFloorAreaSqM.ToString("F0") + " sqm";
            if (maxFloorAreaSqM > 500.0)
                details += " (>500 sqm — additional staircase needed per Reg 47(5)(A)(ii))";
            details += ". Reg 47(5). Only staircases with 'Fire' in Comments are counted.";

            results.Add(new ComplianceResult
            {
                Category = CATEGORY,
                Parameter = "Fire Staircases (count)",
                Required = "≥ " + requiredCount,
                Actual = actualCount.ToString(),
                Status = status,
                Details = details,
                ViolatingElementIds = new ElementId[0]
            });

            // Check enclosure for high-rise buildings (>24m need enclosed staircases)
            if (buildingHeightM > 24.0 && staircaseGroups.Count > 0)
            {
                int enclosedCount = 0;
                var nonEnclosedIds = new List<ElementId>();

                foreach (var group in staircaseGroups)
                {
                    bool enclosed = IsStaircaseEnclosed(doc, group);
                    if (enclosed)
                    {
                        enclosedCount++;
                    }
                    else
                    {
                        nonEnclosedIds.AddRange(group.StairIds);
                    }
                }

                int requiredEnclosed = buildingHeightM > 70.0 ? 2 : 1;
                string encStatus = enclosedCount >= requiredEnclosed ? "PASS" : "FAIL";

                results.Add(new ComplianceResult
                {
                    Category = CATEGORY,
                    Parameter = "Enclosed Fire Staircases",
                    Required = "≥ " + requiredEnclosed + " enclosed",
                    Actual = enclosedCount + " of " + staircaseGroups.Count + " enclosed",
                    Status = encStatus,
                    Details = "High-rise/special buildings require enclosed type staircases. " +
                              "Detection: walls on ≥3 sides of staircase bounding box. Reg 47(5), 48(4)(b).",
                    ViolatingElementIds = encStatus == "FAIL"
                        ? nonEnclosedIds.ToArray()
                        : new ElementId[0]
                });
            }

            return results;
        }

        // ============================================================
        //  CHECK 4: FIRE STAIR WIDTH (EXTENDED) — Reg 47(5), Table 22
        // ============================================================

        private static List<ComplianceResult> CheckFireStairWidth(
            Document doc, DCRParameters parameters,
            List<StaircaseGroup> staircaseGroups,
            double buildingHeightM, string occupancy)
        {
            var results = new List<ComplianceResult>();

            double requiredWidthM = GetFireStairMinWidth(occupancy, buildingHeightM);

            if (staircaseGroups.Count == 0)
            {
                results.Add(new ComplianceResult
                {
                    Category = CATEGORY,
                    Parameter = "Fire Stair Width",
                    Required = "≥ " + requiredWidthM.ToString("F2") + "m",
                    Actual = "No staircases found",
                    Status = "WARNING",
                    Details = "No staircases detected in model. Cannot check fire stair width.",
                    ViolatingElementIds = new ElementId[0]
                });
                return results;
            }

            double narrowestWidthM = double.MaxValue;
            string narrowestName = "";
            var narrowestIds = new List<ElementId>();

            for (int i = 0; i < staircaseGroups.Count; i++)
            {
                StaircaseGroup group = staircaseGroups[i];
                double groupWidthM = GetStaircaseGroupWidth(doc, group) * FEET_TO_METERS;

                if (groupWidthM < narrowestWidthM)
                {
                    narrowestWidthM = groupWidthM;
                    narrowestName = "Staircase " + (i + 1);
                    narrowestIds.Clear();
                    narrowestIds.AddRange(group.StairIds);
                }
            }

            if (narrowestWidthM == double.MaxValue)
                narrowestWidthM = 0;

            string status = narrowestWidthM >= requiredWidthM ? "PASS" : "FAIL";

            string heightContext = "";
            if (buildingHeightM > 70.0)
                heightContext = " (>70m: 2.0m per Reg 47(5)(b))";
            else if (buildingHeightM > 32.0)
                heightContext = " (>32m: enclosed type min 1.5m per Reg 47(5)(A) Note)";

            results.Add(new ComplianceResult
            {
                Category = CATEGORY,
                Parameter = "Fire Stair Width",
                Required = "≥ " + requiredWidthM.ToString("F2") + "m" + heightContext,
                Actual = narrowestWidthM.ToString("F2") + "m (" + narrowestName + ")",
                Status = status,
                Details = "Narrowest staircase: " + narrowestName + " at " +
                          narrowestWidthM.ToString("F2") + "m. " +
                          "Reg 47(5), Table 22." +
                          (status == "FAIL" ? " Short by " +
                          (requiredWidthM - narrowestWidthM).ToString("F2") + "m." : ""),
                ViolatingElementIds = status == "FAIL"
                    ? narrowestIds.ToArray()
                    : new ElementId[0]
            });

            return results;
        }

        // ============================================================
        //  CHECK 5: REFUGE AREA — Reg 47(8)
        // ============================================================

        private static List<ComplianceResult> CheckRefugeArea(
            Document doc, DCRParameters parameters,
            Dictionary<ElementId, List<Room>> roomsByLevel,
            double buildingHeightM)
        {
            var results = new List<ComplianceResult>();

            // Refuge areas only required for buildings > 32m (top level > 32m)
            if (buildingHeightM <= 32.0)
            {
                results.Add(new ComplianceResult
                {
                    Category = CATEGORY,
                    Parameter = "Refuge Area",
                    Required = "N/A (building ≤ 32m)",
                    Actual = "Not Applicable",
                    Status = "PASS",
                    Details = "Building height " + buildingHeightM.ToString("F1") +
                              "m ≤ 32m. Refuge area not required per Reg 47(8).",
                    ViolatingElementIds = new ElementId[0]
                });
                return results;
            }

            // Get ALL levels sorted by elevation (not just levels with rooms)
            var allLevels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            // Build list of ALL habitable levels — every level above ground within building height.
            // Do NOT filter by room presence; rooms may not be placed on every floor.
            var habitableLevels = new List<Level>();
            foreach (Level lev in allLevels)
            {
                double elevM = lev.Elevation * FEET_TO_METERS;
                if (elevM < -0.5) continue; // Skip basements
                habitableLevels.Add(lev);
            }

            // FIRST REFUGE: at 24m with -4m buffer → accept 20m to 28m
            // Then every 7th habitable floor above that.
            double firstRefugeMinM = 20.0; // 24m - 4m buffer
            double firstRefugeMaxM = 28.0; // some upper tolerance

            // Identify which levels SHOULD have refuge rooms
            var refugeRequiredLevels = new List<Level>();
            Level firstRefugeLevel = null;

            // Find the first level at or above 20m (24m - 4m buffer)
            foreach (Level lev in habitableLevels)
            {
                double elevM = lev.Elevation * FEET_TO_METERS;
                if (elevM >= firstRefugeMinM)
                {
                    firstRefugeLevel = lev;
                    refugeRequiredLevels.Add(lev);
                    break;
                }
            }

            // Then every 7th floor above the first refuge (counting ALL levels, not just those with rooms)
            if (firstRefugeLevel != null)
            {
                int floorCountSinceRefuge = 0;
                bool pastFirst = false;

                foreach (Level lev in habitableLevels)
                {
                    if (!pastFirst)
                    {
                        if (lev.Id.Value == firstRefugeLevel.Id.Value)
                            pastFirst = true;
                        continue;
                    }

                    floorCountSinceRefuge++;
                    if (floorCountSinceRefuge >= 7)
                    {
                        refugeRequiredLevels.Add(lev);
                        floorCountSinceRefuge = 0;
                    }
                }
            }

            if (refugeRequiredLevels.Count == 0)
            {
                results.Add(new ComplianceResult
                {
                    Category = CATEGORY,
                    Parameter = "Refuge Area",
                    Required = "First at ~24m (±4m buffer), then every 7th floor",
                    Actual = "No levels ≥ 20m found",
                    Status = "WARNING",
                    Details = "Building >32m but no levels found at or above 20m. " +
                              "Check level elevations. " + habitableLevels.Count + " above-ground levels found.",
                    ViolatingElementIds = new ElementId[0]
                });
                return results;
            }

            // Check each required level for refuge rooms.
            // Rooms may come from roomsByLevel (room-based) OR by scanning all rooms on that level.
            int passCount = 0;
            int failCount = 0;
            string detailLines = "";

            // Also collect ALL rooms indexed by level (in case roomsByLevel is incomplete for this block)
            var allRoomsByLevelFull = GetRoomsByLevel(doc);

            foreach (Level reqLevel in refugeRequiredLevels)
            {
                double elevM = reqLevel.Elevation * FEET_TO_METERS;
                string levelName = reqLevel.Name;

                // Try block-filtered rooms first, then fall back to ALL rooms on this level
                List<Room> levelRooms;
                if (!roomsByLevel.TryGetValue(reqLevel.Id, out levelRooms) || levelRooms.Count == 0)
                {
                    // Fallback: check all rooms on this level (refuge might exist even without block assignment)
                    if (!allRoomsByLevelFull.TryGetValue(reqLevel.Id, out levelRooms))
                    {
                        levelRooms = new List<Room>();
                    }
                }

                double refugeAreaSqFt = 0;
                double habitableAreaSqFt = 0;
                bool refugeRoomFound = false;

                foreach (Room room in levelRooms)
                {
                    if (room == null || room.Area < 1.0)
                        continue;

                    string roomName = "";
                    Parameter nameParam = room.get_Parameter(BuiltInParameter.ROOM_NAME);
                    if (nameParam != null)
                        roomName = nameParam.AsString() ?? "";

                    habitableAreaSqFt += room.Area;

                    if (roomName.IndexOf("Refuge", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        refugeAreaSqFt += room.Area;
                        refugeRoomFound = true;
                    }
                }

                if (!refugeRoomFound)
                {
                    failCount++;
                    detailLines += levelName + " (" + elevM.ToString("F1") + "m): NO Refuge room found; ";
                }
                else
                {
                    double refugeAreaSqM = refugeAreaSqFt * FEET_TO_METERS * FEET_TO_METERS;
                    double habitableAreaSqM = habitableAreaSqFt * FEET_TO_METERS * FEET_TO_METERS;
                    double percentage = habitableAreaSqM > 0
                        ? (refugeAreaSqM / habitableAreaSqM) * 100.0
                        : 0;

                    if (percentage >= 4.0)
                    {
                        passCount++;
                        detailLines += levelName + " (" + elevM.ToString("F1") + "m): OK " +
                                       refugeAreaSqM.ToString("F1") + " sqm = " +
                                       percentage.ToString("F1") + "%; ";
                    }
                    else
                    {
                        failCount++;
                        detailLines += levelName + " (" + elevM.ToString("F1") + "m): UNDER " +
                                       percentage.ToString("F1") + "% (need ≥4%); ";
                    }
                }
            }

            string overallStatus = failCount == 0 ? "PASS" : "FAIL";

            results.Add(new ComplianceResult
            {
                Category = CATEGORY,
                Parameter = "Refuge Area",
                Required = refugeRequiredLevels.Count + " floor(s), each ≥ 4% habitable area",
                Actual = passCount + " pass, " + failCount + " fail of " +
                         refugeRequiredLevels.Count + " required",
                Status = overallStatus,
                Details = "Reg 47(8): First refuge at ~24m (20-24m buffer accepted), then every 7th habitable floor. " +
                          "Habitable = level with Rooms. " + habitableLevels.Count + " habitable level(s) found. " +
                          detailLines.TrimEnd(' ', ';'),
                ViolatingElementIds = new ElementId[0]
            });

            return results;
        }

        // ============================================================
        //  CHECK 6: FIRE DOOR DETECTION — Reg 47(3), 48(2), 48(4)(d)
        // ============================================================

        private static List<ComplianceResult> CheckFireDoors(
            Document doc, DCRParameters parameters,
            List<StaircaseGroup> staircaseGroups,
            List<FamilyInstance> fireRatedDoors,
            double buildingHeightM)
        {
            var results = new List<ComplianceResult>();

            // Fire doors only strictly required for high-rise/special buildings (>24m)
            // But good practice for all buildings
            if (buildingHeightM <= 24.0)
            {
                if (fireRatedDoors.Count > 0)
                {
                    results.Add(new ComplianceResult
                    {
                        Category = CATEGORY,
                        Parameter = "Fire Doors",
                        Required = "Recommended",
                        Actual = fireRatedDoors.Count + " fire-rated doors found",
                        Status = "PASS",
                        Details = "Building ≤24m. Fire doors not strictly mandatory but " +
                                  fireRatedDoors.Count + " found. Reg 48(2).",
                        ViolatingElementIds = new ElementId[0]
                    });
                }
                else
                {
                    results.Add(new ComplianceResult
                    {
                        Category = CATEGORY,
                        Parameter = "Fire Doors",
                        Required = "Recommended",
                        Actual = "None found",
                        Status = "WARNING",
                        Details = "Building ≤24m. Fire-rated doors recommended but not strictly " +
                                  "required. No doors with 'Fire Rating' parameter detected.",
                        ViolatingElementIds = new ElementId[0]
                    });
                }
                return results;
            }

            // For >24m buildings: fire doors required at staircase entries
            // Reg 48(4)(d): Access to main staircases through at least ½ hour
            //   fire-resisting automatic closing doors
            // Check: for each staircase, is there a fire-rated door within proximity?

            if (staircaseGroups.Count == 0)
            {
                results.Add(new ComplianceResult
                {
                    Category = CATEGORY,
                    Parameter = "Fire Doors at Stairs",
                    Required = "Fire doors at all staircase entries",
                    Actual = "No staircases found",
                    Status = "WARNING",
                    Details = "Cannot check fire doors — no staircases detected.",
                    ViolatingElementIds = new ElementId[0]
                });
                return results;
            }

            // Build fire door location map
            var fireDoorLocations = new List<XYZ>();
            foreach (FamilyInstance door in fireRatedDoors)
            {
                LocationPoint lp = door.Location as LocationPoint;
                if (lp != null)
                {
                    fireDoorLocations.Add(lp.Point);
                }
            }

            int staircasesWithFireDoor = 0;
            int staircasesWithoutFireDoor = 0;
            var missingDoorStairIds = new List<ElementId>();
            double searchRadiusFt = 3.0 * METERS_TO_FEET; // 3m search radius

            for (int i = 0; i < staircaseGroups.Count; i++)
            {
                StaircaseGroup group = staircaseGroups[i];
                BoundingBoxXYZ stairBB = GetGroupBoundingBox(doc, group);
                if (stairBB == null)
                    continue;

                // Expand bounding box by search radius to find nearby fire doors
                XYZ bbMin = new XYZ(stairBB.Min.X - searchRadiusFt,
                                    stairBB.Min.Y - searchRadiusFt,
                                    stairBB.Min.Z);
                XYZ bbMax = new XYZ(stairBB.Max.X + searchRadiusFt,
                                    stairBB.Max.Y + searchRadiusFt,
                                    stairBB.Max.Z);

                bool hasFireDoor = false;
                foreach (XYZ doorPt in fireDoorLocations)
                {
                    if (doorPt.X >= bbMin.X && doorPt.X <= bbMax.X &&
                        doorPt.Y >= bbMin.Y && doorPt.Y <= bbMax.Y)
                    {
                        hasFireDoor = true;
                        break;
                    }
                }

                if (hasFireDoor)
                {
                    staircasesWithFireDoor++;
                }
                else
                {
                    staircasesWithoutFireDoor++;
                    missingDoorStairIds.AddRange(group.StairIds);
                }
            }

            string fdStatus = staircasesWithoutFireDoor == 0 ? "PASS" : "FAIL";

            results.Add(new ComplianceResult
            {
                Category = CATEGORY,
                Parameter = "Fire Doors at Staircases",
                Required = "Fire door at each staircase entry",
                Actual = staircasesWithFireDoor + " of " + staircaseGroups.Count +
                         " staircases have fire doors",
                Status = fdStatus,
                Details = fireRatedDoors.Count + " fire-rated door(s) in model. " +
                          "Reg 48(4)(d): ≥½ hr fire-resisting auto-closing doors at staircase access. " +
                          (staircasesWithoutFireDoor > 0
                              ? staircasesWithoutFireDoor + " staircase(s) missing fire doors within 3m."
                              : "All staircases have fire doors nearby."),
                ViolatingElementIds = fdStatus == "FAIL"
                    ? missingDoorStairIds.ToArray()
                    : new ElementId[0]
            });

            // Check for 2-hr fire doors at basement staircases
            if (buildingHeightM > 24.0)
            {
                // Find basement levels (elevation < 0)
                var basementStairIds = new List<ElementId>();
                bool basement2HrFound = false;

                foreach (var group in staircaseGroups)
                {
                    BoundingBoxXYZ bb = GetGroupBoundingBox(doc, group);
                    if (bb != null && bb.Min.Z * FEET_TO_METERS < -0.5) // Below grade
                    {
                        // Check if there's a 2-hr fire door nearby
                        foreach (FamilyInstance door in fireRatedDoors)
                        {
                            string rating = GetFireRating(door);
                            if (rating.Contains("2") && rating.ToUpperInvariant().Contains("HR"))
                            {
                                LocationPoint dlp = door.Location as LocationPoint;
                                if (dlp != null)
                                {
                                    XYZ dp = dlp.Point;
                                    if (dp.X >= bb.Min.X - searchRadiusFt &&
                                        dp.X <= bb.Max.X + searchRadiusFt &&
                                        dp.Y >= bb.Min.Y - searchRadiusFt &&
                                        dp.Y <= bb.Max.Y + searchRadiusFt)
                                    {
                                        basement2HrFound = true;
                                        break;
                                    }
                                }
                            }
                        }

                        if (!basement2HrFound)
                        {
                            basementStairIds.AddRange(group.StairIds);
                        }
                    }
                }

                if (basementStairIds.Count > 0)
                {
                    results.Add(new ComplianceResult
                    {
                        Category = CATEGORY,
                        Parameter = "Basement Fire Doors (2-hr)",
                        Required = "2-hr rated doors at basement staircase",
                        Actual = basement2HrFound ? "Found" : "Not found",
                        Status = basement2HrFound ? "PASS" : "FAIL",
                        Details = "Reg 48(4)(h): Basement staircase access requires 2-hr fire " +
                                  "resistance doors. Check door 'Fire Rating' parameter for '2 Hr'.",
                        ViolatingElementIds = basementStairIds.ToArray()
                    });
                }
            }

            return results;
        }

        // ============================================================
        //  TIER 3 WARNINGS — Manual Verification Required
        // ============================================================

        private static List<ComplianceResult> GetTier3Warnings(double buildingHeightM)
        {
            var warnings = new List<ComplianceResult>();

            // Always applicable
            warnings.Add(new ComplianceResult
            {
                Category = CATEGORY,
                Parameter = "Sprinkler System Layout",
                Required = "Per NBC 2016 Table 7",
                Actual = "Manual check",
                Status = "WARNING",
                Details = "Verify sprinkler layout, coverage, and water supply per NBC 2016 Part 4. " +
                          "Requires MEP model review.",
                ViolatingElementIds = new ElementId[0]
            });

            warnings.Add(new ComplianceResult
            {
                Category = CATEGORY,
                Parameter = "Fire Alarm System",
                Required = "Per Reg 47(3)(ii)(g)",
                Actual = "Manual check",
                Status = "WARNING",
                Details = "High-rise and special buildings: verify fire alarm devices at exits. " +
                          "Requires MEP/electrical model review.",
                ViolatingElementIds = new ElementId[0]
            });

            if (buildingHeightM > 32.0)
            {
                warnings.Add(new ComplianceResult
                {
                    Category = CATEGORY,
                    Parameter = "Wet Riser / Dry Riser",
                    Required = "Per NBC 2016 Table 7",
                    Actual = "Manual check",
                    Status = "WARNING",
                    Details = "Buildings >32m: verify wet riser system with landing valves. " +
                              "Requires MEP model review.",
                    ViolatingElementIds = new ElementId[0]
                });

                warnings.Add(new ComplianceResult
                {
                    Category = CATEGORY,
                    Parameter = "Staircase Pressurization",
                    Required = "Per NBC 2016 Clause 4.10",
                    Actual = "Manual check",
                    Status = "WARNING",
                    Details = "High-rise buildings: enclosed staircases shall be pressurized. " +
                              "Requires HVAC/MEP model review.",
                    ViolatingElementIds = new ElementId[0]
                });
            }

            if (buildingHeightM > 70.0)
            {
                warnings.Add(new ComplianceResult
                {
                    Category = CATEGORY,
                    Parameter = "Fire Escape Chutes",
                    Required = "Per Reg 47(9)(A)",
                    Actual = "Manual check",
                    Status = "WARNING",
                    Details = ">70m: fire escape chute shaft (min 2.5m × 1.5m) required for " +
                              "every wing adjacent to staircase. Walls 4-hr fire resistance.",
                    ViolatingElementIds = new ElementId[0]
                });
            }

            warnings.Add(new ComplianceResult
            {
                Category = CATEGORY,
                Parameter = "Emergency Lighting",
                Required = "Per Reg 47(3)(ii)(d,e)",
                Actual = "Manual check",
                Status = "WARNING",
                Details = "Exits shall be adequately illuminated and clearly visible with " +
                          "signs at 1.5m height. Requires electrical model review.",
                ViolatingElementIds = new ElementId[0]
            });

            warnings.Add(new ComplianceResult
            {
                Category = CATEGORY,
                Parameter = "Structural Fire Resistance",
                Required = "Per NBC 2016 Table 1-3",
                Actual = "Manual check",
                Status = "WARNING",
                Details = "Verify structural fire resistance ratings (columns, beams, slabs) " +
                          "per construction type. Requires structural model review.",
                ViolatingElementIds = new ElementId[0]
            });

            return warnings;
        }

        // ============================================================
        //  HELPER: Staircase Group — lightweight struct for fire safety
        // ============================================================

        public class StaircaseGroup
        {
            public List<ElementId> StairIds { get; set; }
            public XYZ CenterPoint { get; set; }
            public double BaseElevationFt { get; set; }
            public double TopElevationFt { get; set; }

            public StaircaseGroup()
            {
                StairIds = new List<ElementId>();
            }
        }

        // ============================================================
        //  HELPER: Building Block — groups rooms/stairs/doors per building
        //  When a plot has multiple buildings, fire safety checks run per block.
        // ============================================================

        public class BuildingBlock
        {
            public int BlockIndex { get; set; }
            public string Name { get; set; }
            public double MinX { get; set; }
            public double MaxX { get; set; }
            public double MinY { get; set; }
            public double MaxY { get; set; }
            public List<ElementId> RoomIds { get; set; }

            public BuildingBlock()
            {
                RoomIds = new List<ElementId>();
                MinX = double.MaxValue;
                MinY = double.MaxValue;
                MaxX = double.MinValue;
                MaxY = double.MinValue;
            }
        }

        // ============================================================
        //  HELPER: Get building height in meters
        // ============================================================

        private static double GetBuildingHeightMeters(Document doc, DCRParameters parameters)
        {
            // Use the MaxBuildingHeight from parameters if set, otherwise calculate
            if (parameters.MaxBuildingHeight > 0)
            {
                return parameters.MaxBuildingHeight;
            }

            // Fallback: highest level elevation
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToList();

            if (levels.Count == 0)
                return 0;

            double maxElevFt = levels.Max(l => l.Elevation);
            double minElevFt = levels.Min(l => l.Elevation);
            return (maxElevFt - minElevFt) * FEET_TO_METERS;
        }

        // ============================================================
        //  HELPER: Get rooms grouped by level
        // ============================================================

        private static Dictionary<ElementId, List<Room>> GetRoomsByLevel(Document doc)
        {
            var result = new Dictionary<ElementId, List<Room>>();

            // Get all levels first
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .ToList();

            foreach (Level lev in levels)
            {
                result[lev.Id] = new List<Room>();
            }

            // Collect all rooms
            var rooms = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .Cast<Room>()
                .Where(r => r.Area > 0) // Only placed rooms with area
                .ToList();

            foreach (Room room in rooms)
            {
                ElementId levelId = room.LevelId;
                if (levelId != null && levelId != ElementId.InvalidElementId)
                {
                    if (!result.ContainsKey(levelId))
                    {
                        result[levelId] = new List<Room>();
                    }
                    result[levelId].Add(room);
                }
            }

            return result;
        }

        // ============================================================
        //  HELPER: Get staircase groups (simplified union-find)
        // ============================================================

        private static List<StaircaseGroup> GetStaircaseGroups(Document doc)
        {
            var groups = new List<StaircaseGroup>();

            var stairs = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Stairs)
                .OfClass(typeof(Stairs))
                .Cast<Stairs>()
                .ToList();

            if (stairs.Count == 0)
                return groups;

            // Union-Find for grouping stairs with overlapping XY bounding boxes
            int n = stairs.Count;
            int[] parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            // Find root with path compression
            Func<int, int> find = null;
            find = (x) =>
            {
                if (parent[x] != x)
                    parent[x] = find(parent[x]);
                return parent[x];
            };

            // Union
            Action<int, int> union = (a, b) =>
            {
                int ra = find(a);
                int rb = find(b);
                if (ra != rb)
                    parent[ra] = rb;
            };

            // Get bounding boxes
            var bboxes = new BoundingBoxXYZ[n];
            for (int i = 0; i < n; i++)
            {
                bboxes[i] = stairs[i].get_BoundingBox(null);
            }

            // Merge stairs with overlapping XY footprints (150mm tolerance)
            double tolerance = 0.15 * METERS_TO_FEET; // 150mm in feet

            for (int i = 0; i < n; i++)
            {
                if (bboxes[i] == null) continue;
                for (int j = i + 1; j < n; j++)
                {
                    if (bboxes[j] == null) continue;

                    // Check XY overlap with tolerance
                    bool xOverlap = bboxes[i].Min.X - tolerance <= bboxes[j].Max.X &&
                                    bboxes[j].Min.X - tolerance <= bboxes[i].Max.X;
                    bool yOverlap = bboxes[i].Min.Y - tolerance <= bboxes[j].Max.Y &&
                                    bboxes[j].Min.Y - tolerance <= bboxes[i].Max.Y;

                    if (xOverlap && yOverlap)
                    {
                        union(i, j);
                    }
                }
            }

            // Build groups
            var groupMap = new Dictionary<int, StaircaseGroup>();
            for (int i = 0; i < n; i++)
            {
                int root = find(i);
                if (!groupMap.ContainsKey(root))
                {
                    groupMap[root] = new StaircaseGroup();
                }
                groupMap[root].StairIds.Add(stairs[i].Id);

                if (bboxes[i] != null)
                {
                    // Track elevation range
                    double baseZ = bboxes[i].Min.Z;
                    double topZ = bboxes[i].Max.Z;

                    if (groupMap[root].StairIds.Count == 1)
                    {
                        groupMap[root].BaseElevationFt = baseZ;
                        groupMap[root].TopElevationFt = topZ;
                        groupMap[root].CenterPoint = new XYZ(
                            (bboxes[i].Min.X + bboxes[i].Max.X) / 2.0,
                            (bboxes[i].Min.Y + bboxes[i].Max.Y) / 2.0,
                            baseZ);
                    }
                    else
                    {
                        if (baseZ < groupMap[root].BaseElevationFt)
                            groupMap[root].BaseElevationFt = baseZ;
                        if (topZ > groupMap[root].TopElevationFt)
                            groupMap[root].TopElevationFt = topZ;
                    }
                }
            }

            // Filter out minor staircases (ground-to-plinth, split-level steps, etc.)
            // Only keep groups that span at least one full story height (2.4m)
            var filteredGroups = new List<StaircaseGroup>();
            foreach (var group in groupMap.Values)
            {
                double verticalSpanFt = group.TopElevationFt - group.BaseElevationFt;
                if (verticalSpanFt >= MIN_STORY_HEIGHT_FT)
                {
                    filteredGroups.Add(group);
                }
                // else: skip — this is a minor staircase (plinth, split-level, etc.)
            }

            return filteredGroups;
        }

        // ============================================================
        //  HELPER: Get fire-rated doors
        // ============================================================

        private static List<FamilyInstance> GetFireRatedDoors(Document doc)
        {
            var fireRatedDoors = new List<FamilyInstance>();

            var doors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .ToList();

            foreach (FamilyInstance door in doors)
            {
                string rating = GetFireRating(door);
                if (!string.IsNullOrWhiteSpace(rating))
                {
                    fireRatedDoors.Add(door);
                }
            }

            return fireRatedDoors;
        }

        /// <summary>
        /// Reads the "Fire Rating" parameter from a door (type or instance).
        /// Returns empty string if not found or empty.
        /// </summary>
        private static string GetFireRating(FamilyInstance door)
        {
            // Check instance parameter first
            foreach (Parameter p in door.Parameters)
            {
                if (p.Definition != null &&
                    p.Definition.Name.ToUpperInvariant().Contains("FIRE") &&
                    p.Definition.Name.ToUpperInvariant().Contains("RAT"))
                {
                    string val = p.AsString();
                    if (!string.IsNullOrWhiteSpace(val) && val != "None" && val != "0")
                        return val;
                    // Try AsValueString for non-string parameters
                    val = p.AsValueString();
                    if (!string.IsNullOrWhiteSpace(val) && val != "None" && val != "0")
                        return val;
                }
            }

            // Check type parameter
            ElementId typeId = door.GetTypeId();
            if (typeId != null && typeId != ElementId.InvalidElementId)
            {
                Element doorType = door.Document.GetElement(typeId);
                if (doorType != null)
                {
                    foreach (Parameter p in doorType.Parameters)
                    {
                        if (p.Definition != null &&
                            p.Definition.Name.ToUpperInvariant().Contains("FIRE") &&
                            p.Definition.Name.ToUpperInvariant().Contains("RAT"))
                        {
                            string val = p.AsString();
                            if (!string.IsNullOrWhiteSpace(val) && val != "None" && val != "0")
                                return val;
                            val = p.AsValueString();
                            if (!string.IsNullOrWhiteSpace(val) && val != "None" && val != "0")
                                return val;
                        }
                    }
                }
            }

            return "";
        }

        // ============================================================
        //  HELPER: Get exit points per level (stair locations + fire doors)
        // ============================================================

        private static Dictionary<ElementId, List<XYZ>> GetExitPointsByLevel(
            Document doc, List<StaircaseGroup> staircaseGroups,
            List<FamilyInstance> fireRatedDoors)
        {
            var result = new Dictionary<ElementId, List<XYZ>>();

            // Add staircase center points to all levels they span
            var levels = new FilteredElementCollector(doc)
                .OfClass(typeof(Level))
                .Cast<Level>()
                .OrderBy(l => l.Elevation)
                .ToList();

            foreach (var group in staircaseGroups)
            {
                if (group.CenterPoint == null)
                    continue;

                XYZ stairXY = new XYZ(group.CenterPoint.X, group.CenterPoint.Y, 0);

                // Add this stair as an exit point on every level within its elevation range
                foreach (Level lev in levels)
                {
                    double levElev = lev.Elevation;
                    // Stair is an exit on this level if the level is within stair's range
                    if (levElev >= group.BaseElevationFt - 1.0 &&
                        levElev <= group.TopElevationFt + 1.0)
                    {
                        if (!result.ContainsKey(lev.Id))
                            result[lev.Id] = new List<XYZ>();

                        result[lev.Id].Add(new XYZ(group.CenterPoint.X,
                            group.CenterPoint.Y, levElev));
                    }
                }
            }

            // Add fire-rated doors as exit points on their respective levels
            foreach (FamilyInstance door in fireRatedDoors)
            {
                LocationPoint lp = door.Location as LocationPoint;
                if (lp == null) continue;

                ElementId doorLevelId = door.LevelId;
                if (doorLevelId == null || doorLevelId == ElementId.InvalidElementId)
                    continue;

                if (!result.ContainsKey(doorLevelId))
                    result[doorLevelId] = new List<XYZ>();

                result[doorLevelId].Add(lp.Point);
            }

            return result;
        }

        // ============================================================
        //  HELPER: Get test points from a room boundary
        //  Uses actual room boundary segments (not just bounding box)
        //  for accurate travel distance on L-shaped, T-shaped rooms.
        //  Samples points every ~3m along perimeter + centroid.
        // ============================================================

        private static List<XYZ> GetRoomTestPoints(Room room)
        {
            var points = new List<XYZ>();

            // Try LocationPoint (room insertion point / centroid)
            LocationPoint lp = room.Location as LocationPoint;
            if (lp != null)
            {
                points.Add(lp.Point);
            }

            // Try actual room boundary segments — much more accurate than BB
            try
            {
                IList<IList<BoundarySegment>> boundaryLoops =
                    room.GetBoundarySegments(new SpatialElementBoundaryOptions());

                if (boundaryLoops != null && boundaryLoops.Count > 0)
                {
                    // Use the first (outermost) boundary loop
                    IList<BoundarySegment> outerLoop = boundaryLoops[0];
                    double samplingIntervalFt = 3.0 * METERS_TO_FEET; // sample every ~3m

                    foreach (BoundarySegment seg in outerLoop)
                    {
                        Curve curve = seg.GetCurve();
                        if (curve == null) continue;

                        double length = curve.Length;
                        // Add start point
                        points.Add(curve.GetEndPoint(0));

                        // Add intermediate points for long segments
                        if (length > samplingIntervalFt)
                        {
                            int numSamples = (int)(length / samplingIntervalFt);
                            for (int s = 1; s <= numSamples; s++)
                            {
                                double param = (double)s / (numSamples + 1);
                                try
                                {
                                    XYZ midPt = curve.Evaluate(param, true);
                                    points.Add(midPt);
                                }
                                catch
                                {
                                    // Some curves don't support normalized evaluation
                                }
                            }
                        }
                    }

                    // If we got boundary points, return them (skip BB fallback)
                    if (points.Count > 1)
                        return points;
                }
            }
            catch
            {
                // GetBoundarySegments can throw for some room states
            }

            // Fallback: bounding box corners (for rooms without boundary data)
            BoundingBoxXYZ bb = room.get_BoundingBox(null);
            if (bb != null)
            {
                double z = bb.Min.Z;
                points.Add(new XYZ(bb.Min.X, bb.Min.Y, z));
                points.Add(new XYZ(bb.Max.X, bb.Min.Y, z));
                points.Add(new XYZ(bb.Min.X, bb.Max.Y, z));
                points.Add(new XYZ(bb.Max.X, bb.Max.Y, z));
            }

            return points;
        }

        // ============================================================
        //  HELPER: Count doors connecting to a room
        // ============================================================

        private static int CountDoorsInRoom(Document doc, Room room, Phase phase)
        {
            int count = 0;

            var doors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .ToList();

            foreach (FamilyInstance door in doors)
            {
                try
                {
                    Room fromRoom = door.get_FromRoom(phase);
                    Room toRoom = door.get_ToRoom(phase);

                    if ((fromRoom != null && fromRoom.Id == room.Id) ||
                        (toRoom != null && toRoom.Id == room.Id))
                    {
                        count++;
                    }
                }
                catch
                {
                    // FromRoom/ToRoom can throw if phase doesn't match
                    // Fallback: check proximity via bounding box
                    LocationPoint doorLoc = door.Location as LocationPoint;
                    if (doorLoc != null)
                    {
                        BoundingBoxXYZ roomBB = room.get_BoundingBox(null);
                        if (roomBB != null)
                        {
                            XYZ dp = doorLoc.Point;
                            double margin = 0.5; // ~150mm margin in feet
                            if (dp.X >= roomBB.Min.X - margin && dp.X <= roomBB.Max.X + margin &&
                                dp.Y >= roomBB.Min.Y - margin && dp.Y <= roomBB.Max.Y + margin)
                            {
                                count++;
                            }
                        }
                    }
                }
            }

            return count;
        }

        // ============================================================
        //  HELPER: Check if a staircase group is enclosed by walls
        // ============================================================

        private static bool IsStaircaseEnclosed(Document doc, StaircaseGroup group)
        {
            BoundingBoxXYZ stairBB = GetGroupBoundingBox(doc, group);
            if (stairBB == null)
                return false;

            // Strategy: Check for walls within a tolerance on each of 4 sides
            // If walls found on ≥3 sides → considered enclosed
            double tolerance = 1.0 * METERS_TO_FEET; // 1m tolerance in feet

            var walls = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Walls)
                .OfClass(typeof(Wall))
                .Cast<Wall>()
                .ToList();

            bool northWall = false, southWall = false, eastWall = false, westWall = false;

            double minX = stairBB.Min.X;
            double maxX = stairBB.Max.X;
            double minY = stairBB.Min.Y;
            double maxY = stairBB.Max.Y;
            double midX = (minX + maxX) / 2.0;
            double midY = (minY + maxY) / 2.0;

            foreach (Wall wall in walls)
            {
                BoundingBoxXYZ wallBB = wall.get_BoundingBox(null);
                if (wallBB == null) continue;

                // Check Z overlap with stair
                if (wallBB.Max.Z < stairBB.Min.Z || wallBB.Min.Z > stairBB.Max.Z)
                    continue;

                // Determine which side of the staircase this wall is on
                double wallMidX = (wallBB.Min.X + wallBB.Max.X) / 2.0;
                double wallMidY = (wallBB.Min.Y + wallBB.Max.Y) / 2.0;

                // Wall on North side (above maxY, within X range)
                if (wallBB.Min.Y >= maxY - tolerance && wallBB.Min.Y <= maxY + tolerance &&
                    wallBB.Max.X >= minX && wallBB.Min.X <= maxX)
                    northWall = true;

                // Wall on South side (below minY, within X range)
                if (wallBB.Max.Y >= minY - tolerance && wallBB.Max.Y <= minY + tolerance &&
                    wallBB.Max.X >= minX && wallBB.Min.X <= maxX)
                    southWall = true;

                // Wall on East side (right of maxX, within Y range)
                if (wallBB.Min.X >= maxX - tolerance && wallBB.Min.X <= maxX + tolerance &&
                    wallBB.Max.Y >= minY && wallBB.Min.Y <= maxY)
                    eastWall = true;

                // Wall on West side (left of minX, within Y range)
                if (wallBB.Max.X >= minX - tolerance && wallBB.Max.X <= minX + tolerance &&
                    wallBB.Max.Y >= minY && wallBB.Min.Y <= maxY)
                    westWall = true;
            }

            int wallCount = 0;
            if (northWall) wallCount++;
            if (southWall) wallCount++;
            if (eastWall) wallCount++;
            if (westWall) wallCount++;

            return wallCount >= 3; // Enclosed if walls on ≥3 sides (4th can be door)
        }

        // ============================================================
        //  HELPER: Get combined bounding box for a staircase group
        // ============================================================

        private static BoundingBoxXYZ GetGroupBoundingBox(Document doc, StaircaseGroup group)
        {
            double gMinX = double.MaxValue, gMinY = double.MaxValue, gMinZ = double.MaxValue;
            double gMaxX = double.MinValue, gMaxY = double.MinValue, gMaxZ = double.MinValue;
            bool found = false;

            foreach (ElementId id in group.StairIds)
            {
                Element elem = doc.GetElement(id);
                if (elem == null) continue;

                BoundingBoxXYZ bb = elem.get_BoundingBox(null);
                if (bb == null) continue;

                found = true;
                if (bb.Min.X < gMinX) gMinX = bb.Min.X;
                if (bb.Min.Y < gMinY) gMinY = bb.Min.Y;
                if (bb.Min.Z < gMinZ) gMinZ = bb.Min.Z;
                if (bb.Max.X > gMaxX) gMaxX = bb.Max.X;
                if (bb.Max.Y > gMaxY) gMaxY = bb.Max.Y;
                if (bb.Max.Z > gMaxZ) gMaxZ = bb.Max.Z;
            }

            if (!found) return null;

            BoundingBoxXYZ result = new BoundingBoxXYZ();
            result.Min = new XYZ(gMinX, gMinY, gMinZ);
            result.Max = new XYZ(gMaxX, gMaxY, gMaxZ);
            return result;
        }

        // ============================================================
        //  HELPER: Get narrowest width across a staircase group
        //  Uses multiple strategies since Revit exposes stair width differently
        //  depending on family type and version.
        // ============================================================

        private static double GetStaircaseGroupWidth(Document doc, StaircaseGroup group)
        {
            double narrowest = double.MaxValue;

            foreach (ElementId id in group.StairIds)
            {
                Element elem = doc.GetElement(id);
                Stairs stair = elem as Stairs;
                if (stair == null) continue;

                // Strategy 1: Try Stairs-level "Width" or "Actual Run Width" parameter
                double stairLevelWidth = TryGetWidthFromParams(stair);
                if (stairLevelWidth > 0 && stairLevelWidth < narrowest)
                {
                    narrowest = stairLevelWidth;
                    continue; // Got it at stair level, no need to check runs
                }

                // Strategy 2: Check each StairsRun for width parameters
                // NOTE: GetStairsRuns() returns ICollection<ElementId>, not IList
                ICollection<ElementId> runIds = stair.GetStairsRuns();
                foreach (ElementId runId in runIds)
                {
                    Element runElem = doc.GetElement(runId);
                    if (runElem == null) continue;

                    // Try run-level parameters
                    double runWidth = TryGetWidthFromParams(runElem);
                    if (runWidth > 0 && runWidth < narrowest)
                    {
                        narrowest = runWidth;
                        continue;
                    }

                    // Strategy 3: Try BuiltInParameter on the run
                    Parameter bip = runElem.get_Parameter(BuiltInParameter.STAIRS_ACTUAL_TREAD_DEPTH);
                    // Note: STAIRS_ACTUAL_TREAD_DEPTH is tread, not width — just checking availability
                    // Width might be in STAIRS_RUN_ACTUAL_RUN_WIDTH if it exists
                    foreach (Parameter p in runElem.GetOrderedParameters())
                    {
                        if (p.Definition == null) continue;
                        string pName = p.Definition.Name;
                        // Match any of: "Width", "Actual Run Width", "Actual Tread Width",
                        //               "Minimum Run Width", "Run Width"
                        if (pName.IndexOf("Width", StringComparison.OrdinalIgnoreCase) >= 0 &&
                            pName.IndexOf("Tread", StringComparison.OrdinalIgnoreCase) < 0) // exclude tread depth
                        {
                            double w = p.AsDouble();
                            if (w > 0 && w < narrowest)
                            {
                                narrowest = w;
                                break;
                            }
                        }
                    }

                    // Strategy 4: Fallback — use StairsRun path curve offset (perpendicular width)
                    StairsRun run = runElem as StairsRun;
                    if (run != null && narrowest == double.MaxValue)
                    {
                        try
                        {
                            CurveLoop path = run.GetStairsPath();
                            if (path != null)
                            {
                                // The run bounding box perpendicular to the path = width
                                BoundingBoxXYZ runBB = runElem.get_BoundingBox(null);
                                if (runBB != null)
                                {
                                    // Get path direction to determine which BB axis is width
                                    double dx = runBB.Max.X - runBB.Min.X;
                                    double dy = runBB.Max.Y - runBB.Min.Y;
                                    // Width is the shorter XY dimension
                                    double bbWidth = Math.Min(dx, dy);
                                    if (bbWidth > 0.1 && bbWidth < narrowest) // min 30mm sanity check
                                    {
                                        narrowest = bbWidth;
                                    }
                                }
                            }
                        }
                        catch
                        {
                            // GetStairsPath can throw for some stair types
                        }
                    }

                    // Strategy 5: Last resort — bounding box shorter XY dimension
                    if (narrowest == double.MaxValue)
                    {
                        BoundingBoxXYZ runBB = runElem.get_BoundingBox(null);
                        if (runBB != null)
                        {
                            double dx = runBB.Max.X - runBB.Min.X;
                            double dy = runBB.Max.Y - runBB.Min.Y;
                            double bbWidth = Math.Min(dx, dy);
                            if (bbWidth > 0.1 && bbWidth < narrowest)
                            {
                                narrowest = bbWidth;
                            }
                        }
                    }
                }

                // Strategy 6: If runs gave nothing, try the Stairs type parameters
                if (narrowest == double.MaxValue)
                {
                    ElementId typeId = stair.GetTypeId();
                    if (typeId != null && typeId != ElementId.InvalidElementId)
                    {
                        Element stairType = doc.GetElement(typeId);
                        if (stairType != null)
                        {
                            double typeWidth = TryGetWidthFromParams(stairType);
                            if (typeWidth > 0)
                                narrowest = typeWidth;
                        }
                    }
                }
            }

            return narrowest == double.MaxValue ? 0 : narrowest;
        }

        /// <summary>
        /// Tries to extract a width value from an element's parameters.
        /// Searches for parameter names containing "Width" (excluding "Tread").
        /// Returns 0 if not found.
        /// </summary>
        private static double TryGetWidthFromParams(Element elem)
        {
            foreach (Parameter p in elem.Parameters)
            {
                if (p.Definition == null) continue;
                string pName = p.Definition.Name;

                // Match: "Width", "Actual Run Width", "Run Width", "Minimum Run Width",
                //        "Actual Width", "Minimum Stair Width"
                // Exclude: "Tread Width" (that's tread depth, not stair width)
                bool hasWidth = pName.IndexOf("Width", StringComparison.OrdinalIgnoreCase) >= 0;
                bool hasTread = pName.IndexOf("Tread", StringComparison.OrdinalIgnoreCase) >= 0;

                if (hasWidth && !hasTread)
                {
                    double val = p.AsDouble();
                    if (val > 0.1) // > ~30mm sanity check (in feet)
                        return val;
                }
            }
            return 0;
        }

        // ============================================================
        //  HELPER: Detect building blocks from room spatial clustering
        //  Uses union-find on room XY bounding boxes to group rooms
        //  into separate building blocks when multiple buildings exist.
        // ============================================================

        private static List<BuildingBlock> DetectBuildingBlocks(
            Document doc, Dictionary<ElementId, List<Room>> roomsByLevel)
        {
            // ── WALL-BASED CLUSTERING (no room dependency) ──
            // Collect all above-ground walls and cluster by XY bounding box proximity.
            // Walls within same building connect at corners/junctions (within ~1m).
            // Separate buildings have a gap >> 1m between them.

            var walls = new FilteredElementCollector(doc)
                .OfClass(typeof(Wall))
                .WhereElementIsNotElementType()
                .Cast<Wall>()
                .ToList();

            // Filter: only above-ground walls (top of wall above -0.5m)
            var aboveGroundWalls = new List<Wall>();
            var wallBBs = new List<BoundingBoxXYZ>();
            foreach (Wall w in walls)
            {
                BoundingBoxXYZ bb = w.get_BoundingBox(null);
                if (bb == null) continue;
                if (bb.Max.Z * FEET_TO_METERS < -0.5) continue; // below grade
                aboveGroundWalls.Add(w);
                wallBBs.Add(bb);
            }

            // If fewer than 2 walls, return single block
            if (aboveGroundWalls.Count < 2)
            {
                var singleBlock = CreateSingleBlock(roomsByLevel);
                return new List<BuildingBlock> { singleBlock };
            }

            // Union-find: merge walls whose XY bounding boxes overlap or are within 1m
            int n = aboveGroundWalls.Count;
            int[] parent = new int[n];
            for (int i = 0; i < n; i++) parent[i] = i;

            Func<int, int> find = null;
            find = (x) =>
            {
                if (parent[x] != x)
                    parent[x] = find(parent[x]);
                return parent[x];
            };

            Action<int, int> union = (a, b) =>
            {
                int ra = find(a);
                int rb = find(b);
                if (ra != rb)
                    parent[ra] = rb;
            };

            double tolerance = 1.0 * METERS_TO_FEET; // 1m — walls at corners are within mm

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    // XY bounding box proximity check (ignore Z — same building spans floors)
                    bool xNear = wallBBs[i].Min.X - tolerance <= wallBBs[j].Max.X &&
                                 wallBBs[j].Min.X - tolerance <= wallBBs[i].Max.X;
                    bool yNear = wallBBs[i].Min.Y - tolerance <= wallBBs[j].Max.Y &&
                                 wallBBs[j].Min.Y - tolerance <= wallBBs[i].Max.Y;

                    if (xNear && yNear)
                    {
                        union(i, j);
                    }
                }
            }

            // Build block envelopes from wall clusters
            var blockMap = new Dictionary<int, BuildingBlock>();
            for (int i = 0; i < n; i++)
            {
                int root = find(i);
                if (!blockMap.ContainsKey(root))
                {
                    blockMap[root] = new BuildingBlock();
                }

                var block = blockMap[root];
                BoundingBoxXYZ bb = wallBBs[i];
                if (bb.Min.X < block.MinX) block.MinX = bb.Min.X;
                if (bb.Min.Y < block.MinY) block.MinY = bb.Min.Y;
                if (bb.Max.X > block.MaxX) block.MaxX = bb.Max.X;
                if (bb.Max.Y > block.MaxY) block.MaxY = bb.Max.Y;
            }

            var blocks = blockMap.Values.ToList();

            // If clustering produced only 1 block, return it
            if (blocks.Count <= 1)
            {
                var singleBlock = blocks.Count == 1 ? blocks[0] : new BuildingBlock();
                singleBlock.BlockIndex = 0;
                singleBlock.Name = "";
                // Assign ALL rooms to this single block
                foreach (var kvp in roomsByLevel)
                {
                    foreach (Room r in kvp.Value)
                    {
                        if (r != null && r.Area >= 1.0)
                            singleBlock.RoomIds.Add(r.Id);
                    }
                }
                return new List<BuildingBlock> { singleBlock };
            }

            // ── Assign rooms to blocks by XY containment ──
            double margin = 2.0 * METERS_TO_FEET; // 2m margin for room assignment
            foreach (var kvp in roomsByLevel)
            {
                foreach (Room r in kvp.Value)
                {
                    if (r == null || r.Area < 1.0) continue;
                    BoundingBoxXYZ rbb = r.get_BoundingBox(null);
                    if (rbb == null) continue;

                    double cx = (rbb.Min.X + rbb.Max.X) / 2.0;
                    double cy = (rbb.Min.Y + rbb.Max.Y) / 2.0;

                    // Find the block whose envelope contains this room's center
                    BuildingBlock bestBlock = null;
                    double bestDist = double.MaxValue;
                    foreach (BuildingBlock b in blocks)
                    {
                        if (cx >= b.MinX - margin && cx <= b.MaxX + margin &&
                            cy >= b.MinY - margin && cy <= b.MaxY + margin)
                        {
                            // If within envelope, pick the closest block center
                            double bcx = (b.MinX + b.MaxX) / 2.0;
                            double bcy = (b.MinY + b.MaxY) / 2.0;
                            double dist = Math.Sqrt((cx - bcx) * (cx - bcx) + (cy - bcy) * (cy - bcy));
                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                bestBlock = b;
                            }
                        }
                    }

                    if (bestBlock != null)
                    {
                        bestBlock.RoomIds.Add(r.Id);
                    }
                    else
                    {
                        // Room outside all envelopes — assign to nearest block
                        foreach (BuildingBlock b in blocks)
                        {
                            double bcx = (b.MinX + b.MaxX) / 2.0;
                            double bcy = (b.MinY + b.MaxY) / 2.0;
                            double dist = Math.Sqrt((cx - bcx) * (cx - bcx) + (cy - bcy) * (cy - bcy));
                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                bestBlock = b;
                            }
                        }
                        if (bestBlock != null)
                            bestBlock.RoomIds.Add(r.Id);
                    }
                }
            }

            // Name blocks
            for (int i = 0; i < blocks.Count; i++)
            {
                blocks[i].BlockIndex = i;
                if (blocks.Count > 1)
                    blocks[i].Name = "Block " + (char)('A' + Math.Min(i, 25));
                else
                    blocks[i].Name = "";
            }

            return blocks;
        }

        /// <summary>
        /// Helper: create a single block containing all rooms (when only one building detected).
        /// </summary>
        private static BuildingBlock CreateSingleBlock(Dictionary<ElementId, List<Room>> roomsByLevel)
        {
            var singleBlock = new BuildingBlock
            {
                BlockIndex = 0,
                Name = ""
            };
            foreach (var kvp in roomsByLevel)
            {
                foreach (Room r in kvp.Value)
                {
                    if (r != null && r.Area >= 1.0)
                    {
                        singleBlock.RoomIds.Add(r.Id);
                        BoundingBoxXYZ rbb = r.get_BoundingBox(null);
                        if (rbb != null)
                        {
                            if (rbb.Min.X < singleBlock.MinX) singleBlock.MinX = rbb.Min.X;
                            if (rbb.Min.Y < singleBlock.MinY) singleBlock.MinY = rbb.Min.Y;
                            if (rbb.Max.X > singleBlock.MaxX) singleBlock.MaxX = rbb.Max.X;
                            if (rbb.Max.Y > singleBlock.MaxY) singleBlock.MaxY = rbb.Max.Y;
                        }
                    }
                }
            }
            return singleBlock;
        }

        // ============================================================
        //  HELPER: Filter staircase groups to only fire-marked staircases
        //  A staircase is a "Fire Staircase" if any Stairs element in the
        //  group has "Fire" in its Comments parameter (Element Identity).
        // ============================================================

        private static List<StaircaseGroup> FilterFireStaircases(
            Document doc, List<StaircaseGroup> allGroups)
        {
            var fireGroups = new List<StaircaseGroup>();
            foreach (StaircaseGroup group in allGroups)
            {
                bool isFire = false;
                foreach (ElementId id in group.StairIds)
                {
                    Element elem = doc.GetElement(id);
                    if (elem == null) continue;

                    // Check Comments parameter for "Fire"
                    Parameter comments = elem.get_Parameter(
                        BuiltInParameter.ALL_MODEL_INSTANCE_COMMENTS);
                    if (comments != null)
                    {
                        string val = comments.AsString() ?? "";
                        if (val.IndexOf("Fire", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            isFire = true;
                            break;
                        }
                    }
                }
                if (isFire)
                {
                    fireGroups.Add(group);
                }
            }
            return fireGroups;
        }

        // ============================================================
        //  HELPER: Filter rooms to a specific building block
        // ============================================================

        private static Dictionary<ElementId, List<Room>> FilterRoomsToBlock(
            Dictionary<ElementId, List<Room>> roomsByLevel, BuildingBlock block)
        {
            var blockRoomSet = new HashSet<long>();
            foreach (ElementId id in block.RoomIds)
            {
                blockRoomSet.Add(id.Value);
            }

            var result = new Dictionary<ElementId, List<Room>>();
            foreach (var kvp in roomsByLevel)
            {
                var filtered = new List<Room>();
                foreach (Room r in kvp.Value)
                {
                    if (r != null && blockRoomSet.Contains(r.Id.Value))
                    {
                        filtered.Add(r);
                    }
                }
                if (filtered.Count > 0)
                {
                    result[kvp.Key] = filtered;
                }
            }
            return result;
        }

        // ============================================================
        //  HELPER: Filter staircase groups to a building block (by XY position)
        // ============================================================

        private static List<StaircaseGroup> FilterStaircasesToBlock(
            List<StaircaseGroup> groups, BuildingBlock block)
        {
            double margin = 1.0 * METERS_TO_FEET; // 1m tolerance
            var result = new List<StaircaseGroup>();

            foreach (StaircaseGroup g in groups)
            {
                if (g.CenterPoint == null) continue;
                if (g.CenterPoint.X >= block.MinX - margin &&
                    g.CenterPoint.X <= block.MaxX + margin &&
                    g.CenterPoint.Y >= block.MinY - margin &&
                    g.CenterPoint.Y <= block.MaxY + margin)
                {
                    result.Add(g);
                }
            }
            return result;
        }

        // ============================================================
        //  HELPER: Filter fire-rated doors to a building block (by XY position)
        // ============================================================

        private static List<FamilyInstance> FilterDoorsToBlock(
            List<FamilyInstance> doors, BuildingBlock block)
        {
            double margin = 1.0 * METERS_TO_FEET;
            var result = new List<FamilyInstance>();

            foreach (FamilyInstance door in doors)
            {
                LocationPoint lp = door.Location as LocationPoint;
                if (lp == null) continue;
                if (lp.Point.X >= block.MinX - margin &&
                    lp.Point.X <= block.MaxX + margin &&
                    lp.Point.Y >= block.MinY - margin &&
                    lp.Point.Y <= block.MaxY + margin)
                {
                    result.Add(door);
                }
            }
            return result;
        }

        // ============================================================
        //  HELPER: Prefix block name to result parameters
        // ============================================================

        private static void PrefixBlockName(List<ComplianceResult> results, string blockName)
        {
            if (string.IsNullOrEmpty(blockName)) return;
            foreach (ComplianceResult r in results)
            {
                r.Parameter = blockName + " — " + r.Parameter;
            }
        }

        // ============================================================
        //  HELPER: Get last phase in document
        // ============================================================

        private static Phase GetLastPhase(Document doc)
        {
            PhaseArray phases = doc.Phases;
            if (phases == null || phases.Size == 0)
                return null;
            return phases.get_Item(phases.Size - 1);
        }

        // ============================================================
        //  HELPER: Get level name from ElementId
        // ============================================================

        private static string GetLevelName(Document doc, ElementId levelId)
        {
            if (levelId == null || levelId == ElementId.InvalidElementId)
                return "Unknown Level";

            Element elem = doc.GetElement(levelId);
            if (elem != null)
                return elem.Name;

            return "Level " + levelId.Value.ToString();
        }
    }
}
