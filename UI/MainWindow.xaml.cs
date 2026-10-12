using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using MumbaiDCRChecker.Models;
using MumbaiDCRChecker.Services;
using MumbaiDCRChecker.Utilities;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MumbaiDCRChecker.UI
{
    public partial class MainWindow : Window
    {
        private readonly Document _doc;
        private readonly UIDocument _uidoc;
        private DCRParameters _dcrParams;
        private List<ComplianceResult> _results;
        private ObservableCollection<DCRParameterRow> _parameterRows;

        public MainWindow(Document doc, UIDocument uidoc)
        {
            InitializeComponent();
            _doc = doc ?? throw new ArgumentNullException(nameof(doc));
            _uidoc = uidoc ?? throw new ArgumentNullException(nameof(uidoc));
            _dcrParams = new DCRParameters();
            _results = new List<ComplianceResult>();
            _parameterRows = new ObservableCollection<DCRParameterRow>();

            LoadLevels();
            DCRTab.IsEnabled = false;
            ResultsTab.IsEnabled = false;
        }

        private void LoadLevels()
        {
            try
            {
                FilteredElementCollector collector = new FilteredElementCollector(_doc);
                ICollection<Element> levels = collector.OfClass(typeof(Level)).ToElements();

                var levelNames = levels.Cast<Level>().OrderBy(l => l.Elevation).Select(l => l.Name).ToList();

                GroundLevelCombo.ItemsSource = levelNames;
                TopLevelCombo.ItemsSource = levelNames;

                if (GroundLevelCombo.Items.Count > 0) 
                    GroundLevelCombo.SelectedIndex = 0;
                
                if (TopLevelCombo.Items.Count > 1) 
                    TopLevelCombo.SelectedIndex = TopLevelCombo.Items.Count - 1;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error loading levels: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void NextButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Validate inputs
                if (!ValidateProjectInfo())
                    return;

                // Collect project information
                _dcrParams.ZoneType = ((ComboBoxItem)ZoneTypeCombo.SelectedItem).Content.ToString() ?? "City";
                _dcrParams.ProjectType = ((ComboBoxItem)ProjectTypeCombo.SelectedItem).Content.ToString() ?? "Residential";
                
                if (!double.TryParse(NorthRoadWidthBox.Text, out double northWidth) || northWidth <= 0)
                {
                    MessageBox.Show("Please enter a valid Front Road Width (positive number). Every plot must have at least one abutting road.", "Validation Error");
                    return;
                }
                if (!double.TryParse(SouthRoadWidthBox.Text, out double southWidth) || southWidth < 0)
                {
                    MessageBox.Show("Please enter a valid Rear Road Width (0 if no road on this side)", "Validation Error");
                    return;
                }
                if (!double.TryParse(EastRoadWidthBox.Text, out double eastWidth) || eastWidth < 0)
                {
                    MessageBox.Show("Please enter a valid Side 1 Road Width (0 if no road on this side)", "Validation Error");
                    return;
                }
                if (!double.TryParse(WestRoadWidthBox.Text, out double westWidth) || westWidth < 0)
                {
                    MessageBox.Show("Please enter a valid Side 2 Road Width (0 if no road on this side)", "Validation Error");
                    return;
                }

                _dcrParams.NorthRoadWidth = northWidth;
                _dcrParams.SouthRoadWidth = southWidth;
                _dcrParams.EastRoadWidth = eastWidth;
                _dcrParams.WestRoadWidth = westWidth;

                // Parse internal road widths (default 0 = no internal road)
                double intFront = 0, intRear = 0, intSide1 = 0, intSide2 = 0;
                double.TryParse(IntRoadFrontBox.Text, out intFront);
                double.TryParse(IntRoadRearBox.Text, out intRear);
                double.TryParse(IntRoadSide1Box.Text, out intSide1);
                double.TryParse(IntRoadSide2Box.Text, out intSide2);
                _dcrParams.InternalRoadWidthFront = Math.Max(0, intFront);
                _dcrParams.InternalRoadWidthRear = Math.Max(0, intRear);
                _dcrParams.InternalRoadWidthSide1 = Math.Max(0, intSide1);
                _dcrParams.InternalRoadWidthSide2 = Math.Max(0, intSide2);

                _dcrParams.GroundLevelName = GroundLevelCombo.SelectedItem?.ToString() ?? string.Empty;
                _dcrParams.TopLevelName = TopLevelCombo.SelectedItem?.ToString() ?? string.Empty;

                // Phase 4: Collect fire safety inputs
                _dcrParams.HasSprinklers = SprinklerCheckBox.IsChecked == true;
                _dcrParams.Occupancy = ((ComboBoxItem)OccupancyCombo.SelectedItem)?.Content?.ToString() ?? "Residential";

                // Populate DCR parameters
                PopulateDCRParameters();

                // Enable and switch to DCR tab
                DCRTab.IsEnabled = true;
                MainTabControl.SelectedItem = DCRTab;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error: {ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool ValidateProjectInfo()
        {
            if (GroundLevelCombo.SelectedItem == null)
            {
                MessageBox.Show("Please select a Ground Level", "Validation Error");
                return false;
            }

            if (TopLevelCombo.SelectedItem == null)
            {
                MessageBox.Show("Please select a Top Level", "Validation Error");
                return false;
            }

            return true;
        }

        private void PopulateDCRParameters()
        {
            _parameterRows.Clear();

            // Calculate setbacks based on road widths
            _dcrParams.FrontSetback = MumbaiDCRData.GetSetbackForRoadWidth(_dcrParams.NorthRoadWidth);
            _dcrParams.RearSetback = MumbaiDCRData.GetSetbackForRoadWidth(_dcrParams.SouthRoadWidth);
            _dcrParams.SideSetback1 = MumbaiDCRData.GetSetbackForRoadWidth(_dcrParams.EastRoadWidth);
            _dcrParams.SideSetback2 = MumbaiDCRData.GetSetbackForRoadWidth(_dcrParams.WestRoadWidth);

            // Get other DCR parameters
            _dcrParams.MaxBuildingHeight = MumbaiDCRData.GetMaxBuildingHeight(_dcrParams.ProjectType, _dcrParams.ZoneType);
            _dcrParams.GroundCoveragePercent = MumbaiDCRData.GetGroundCoverageLimit(_dcrParams.ProjectType, _dcrParams.ZoneType);
            _dcrParams.OpenSpacePercent = MumbaiDCRData.GetOpenSpaceRequirement(_dcrParams.ProjectType);
            _dcrParams.MinInterBuildingDistance = MumbaiDCRData.GetMinInterBuildingDistance(_dcrParams.ProjectType);

            // Parking calculation (simplified - using 1000 sqm as example built-up area)
            _dcrParams.ParkingSpacesRequired = MumbaiDCRData.CalculateParkingSpaces(_dcrParams.ProjectType, 1000);

            // Populate grid
            string intFrontNote = _dcrParams.InternalRoadWidthFront > 0 
                ? $" + Int. road {_dcrParams.InternalRoadWidthFront}m" : "";
            string intRearNote = _dcrParams.InternalRoadWidthRear > 0 
                ? $" + Int. road {_dcrParams.InternalRoadWidthRear}m" : "";
            string intSide1Note = _dcrParams.InternalRoadWidthSide1 > 0 
                ? $" + Int. road {_dcrParams.InternalRoadWidthSide1}m" : "";
            string intSide2Note = _dcrParams.InternalRoadWidthSide2 > 0 
                ? $" + Int. road {_dcrParams.InternalRoadWidthSide2}m" : "";

            _parameterRows.Add(new DCRParameterRow 
            { 
                Parameter = "Front Setback", 
                RequiredValue = _dcrParams.FrontSetback, 
                Unit = "m", 
                Notes = $"Based on {_dcrParams.NorthRoadWidth}m road width" + intFrontNote
            });

            _parameterRows.Add(new DCRParameterRow 
            { 
                Parameter = "Rear Setback", 
                RequiredValue = _dcrParams.RearSetback, 
                Unit = "m", 
                Notes = _dcrParams.SouthRoadWidth > 0 
                    ? $"Based on {_dcrParams.SouthRoadWidth}m road width" + intRearNote
                    : "No road - marginal open space (Reg 41(2))" + intRearNote
            });

            _parameterRows.Add(new DCRParameterRow 
            { 
                Parameter = "Side 1 Setback", 
                RequiredValue = _dcrParams.SideSetback1, 
                Unit = "m", 
                Notes = _dcrParams.EastRoadWidth > 0 
                    ? $"Based on {_dcrParams.EastRoadWidth}m road width" + intSide1Note
                    : "No road - marginal open space (Reg 41(2))" + intSide1Note
            });

            _parameterRows.Add(new DCRParameterRow 
            { 
                Parameter = "Side 2 Setback", 
                RequiredValue = _dcrParams.SideSetback2, 
                Unit = "m", 
                Notes = _dcrParams.WestRoadWidth > 0 
                    ? $"Based on {_dcrParams.WestRoadWidth}m road width" + intSide2Note
                    : "No road - marginal open space (Reg 41(2))" + intSide2Note 
            });

            _parameterRows.Add(new DCRParameterRow 
            { 
                Parameter = "Max Building Height", 
                RequiredValue = _dcrParams.MaxBuildingHeight, 
                Unit = "m", 
                Notes = $"{_dcrParams.ProjectType} in {_dcrParams.ZoneType}" 
            });

            _parameterRows.Add(new DCRParameterRow 
            { 
                Parameter = "Ground Coverage", 
                RequiredValue = _dcrParams.GroundCoveragePercent, 
                Unit = "%", 
                Notes = "Maximum allowed" 
            });

            _parameterRows.Add(new DCRParameterRow 
            { 
                Parameter = "Parking Spaces", 
                RequiredValue = _dcrParams.ParkingSpacesRequired, 
                Unit = "ECS", 
                Notes = "Manual count required (Phase 1)" 
            });

            _parameterRows.Add(new DCRParameterRow 
            { 
                Parameter = "Open Space", 
                RequiredValue = _dcrParams.OpenSpacePercent, 
                Unit = "%", 
                Notes = "Minimum required" 
            });

            _parameterRows.Add(new DCRParameterRow 
            { 
                Parameter = "Inter-Building Distance", 
                RequiredValue = _dcrParams.MinInterBuildingDistance, 
                Unit = "m", 
                Notes = "Minimum between buildings" 
            });

            DCRParametersGrid.ItemsSource = _parameterRows;
        }

        private void BackButton_Click(object sender, RoutedEventArgs e)
        {
            MainTabControl.SelectedIndex = 0;
        }

        private void RunCheckButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                // Update DCR parameters from grid (in case user edited)
                UpdateDCRParametersFromGrid();

                // Run compliance checks
                RunComplianceChecks();

                // Show results with category grouping
                var view = new System.Windows.Data.ListCollectionView(_results);
                view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription("Category"));
                ResultsGrid.ItemsSource = view;
                ResultsTab.IsEnabled = true;
                MainTabControl.SelectedItem = ResultsTab;
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error running compliance check:\n{ex.Message}", "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void UpdateDCRParametersFromGrid()
        {
            if (_parameterRows.Count >= 9)
            {
                _dcrParams.FrontSetback = _parameterRows[0].RequiredValue;
                _dcrParams.RearSetback = _parameterRows[1].RequiredValue;
                _dcrParams.SideSetback1 = _parameterRows[2].RequiredValue;
                _dcrParams.SideSetback2 = _parameterRows[3].RequiredValue;
                _dcrParams.MaxBuildingHeight = _parameterRows[4].RequiredValue;
                _dcrParams.GroundCoveragePercent = _parameterRows[5].RequiredValue;
                _dcrParams.ParkingSpacesRequired = (int)_parameterRows[6].RequiredValue;
                _dcrParams.OpenSpacePercent = _parameterRows[7].RequiredValue;
                _dcrParams.MinInterBuildingDistance = _parameterRows[8].RequiredValue;
            }
        }

        private void RunComplianceChecks()
        {
            _results.Clear();

            try
            {
                // 1. Check plot boundary
                var plotAnalyzer = new PlotAnalyzer(_doc);
                var plotBoundary = plotAnalyzer.GetPlotBoundary();
                
                if (plotBoundary == null)
                {
                    MessageBox.Show(
                        "Plot boundary not found or invalid.\n\n" +
                        "Please ensure:\n" +
                        "1. Property lines are drawn (Massing & Site → Property Line)\n" +
                        "2. Lines form a closed boundary\n" +
                        "3. All segments connect end-to-end", 
                        "Plot Boundary Error",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                // 2. Check setbacks - Try Reference Planes first, fall back to automatic
                var refPlaneChecker = new RefPlaneSetbackChecker(_doc);
                var refPlaneResults = refPlaneChecker.TryCheckWithRefPlanes(_dcrParams);

                if (refPlaneResults != null)
                {
                    TagCategory(refPlaneResults, "Setbacks");
                    _results.AddRange(refPlaneResults);
                }
                else
                {
                    var setbackChecker = new SetbackChecker(_doc);
                    var setbackResults = setbackChecker.CheckSetbacks(_dcrParams, plotBoundary);
                    TagCategory(setbackResults, "Setbacks");
                    _results.AddRange(setbackResults);
                }

                // 3. Check building height
                var heightChecker = new HeightChecker(_doc);
                var heightResult = heightChecker.CheckHeight(_dcrParams);
                heightResult.Category = "Building Height";
                _results.Add(heightResult);

                // 4. Check ground coverage
                var coverageChecker = new CoverageChecker(_doc);
                var coverageResult = coverageChecker.CheckGroundCoverage(_dcrParams, plotBoundary);
                coverageResult.Category = "Coverage & Open Space";
                _results.Add(coverageResult);

                // 5. Check open space
                var openSpaceResult = coverageChecker.CheckOpenSpace(_dcrParams, plotBoundary);
                openSpaceResult.Category = "Coverage & Open Space";
                _results.Add(openSpaceResult);

                // 6. Check inter-building distance
                var interBuildingChecker = new InterBuildingChecker(_doc);
                var ibResult = interBuildingChecker.CheckInterBuildingDistance(_dcrParams);
                ibResult.Category = "Inter-Building Distance";
                _results.Add(ibResult);

                // 7. Check Recreational Ground (RG) - DCPR 2034 Regulation 27
                double plotAreaSqm = plotAnalyzer.GetPlotArea(plotBoundary);
                var rgChecker = new RGChecker(_doc);
                var rgResults = rgChecker.CheckRG(_dcrParams, plotAreaSqm);
                TagCategory(rgResults, "Recreational Ground (RG)");
                _results.AddRange(rgResults);

                // 8. Check Staircase Compliance - DCPR 2034 Regulation 37(13)
                var staircaseChecker = new StaircaseChecker(_doc);
                var stairResults = staircaseChecker.CheckStaircases(_dcrParams);
                TagCategory(stairResults, "Staircase");
                _results.AddRange(stairResults);

                // 9. Parking (placeholder)
                _results.Add(new ComplianceResult
                {
                    Category = "Parking",
                    Parameter = "Parking Spaces",
                    Required = _dcrParams.ParkingSpacesRequired.ToString(),
                    Actual = "Manual Count Required",
                    Status = "WARNING",
                    Details = "Parking detection not implemented. Please verify manually."
                });

                // 10. Phase 4: Fire Safety Compliance - DCPR 2034 Reg 47-48
                var fireSafetyResults = FireSafetyChecker.CheckAll(_doc, _dcrParams);
                _results.AddRange(fireSafetyResults);

                // Show summary
                int passed = _results.Count(r => r.Status == "PASS");
                int failed = _results.Count(r => r.Status == "FAIL");
                int warnings = _results.Count(r => r.Status == "WARNING");

                string summary = $"Compliance Check Completed!\n\n" +
                                $"✓ Passed: {passed}\n" +
                                $"✗ Failed: {failed}\n" +
                                $"⚠ Warnings: {warnings}";

                MessageBox.Show(summary, "Check Complete", MessageBoxButton.OK, 
                    failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error during compliance check:\n{ex.Message}\n\nStack Trace:\n{ex.StackTrace}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void HighlightButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (_results == null || _results.Count == 0)
                {
                    MessageBox.Show("No results to highlight. Please run compliance check first.", 
                        "No Results", MessageBoxButton.OK, MessageBoxImage.Information);
                    return;
                }

                var highlighter = new ElementHighlighter(_doc, _uidoc);
                highlighter.HighlightViolations(_results);
                
                int highlightedCount = _results.Count(r => r.Status == "FAIL" && r.ViolatingElementIds != null && r.ViolatingElementIds.Length > 0);
                
                MessageBox.Show(
                    $"Violations highlighted in 3D view!\n\n" +
                    $"Red = Failed elements (walls, stairs, slabs, railings, rooms, doors)\n\n" +
                    $"Failed checks with highlighted elements: {highlightedCount}", 
                    "Highlighting Complete", 
                    MessageBoxButton.OK, 
                    MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error highlighting elements:\n{ex.Message}", 
                    "Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void ExportButton_Click(object sender, RoutedEventArgs e)
        {
            MessageBox.Show(
                "Export functionality will be implemented in Phase 2.\n\n" +
                "Planned features:\n" +
                "• Export to Excel\n" +
                "• Export to PDF report\n" +
                "• Include screenshots of violations", 
                "Coming Soon", 
                MessageBoxButton.OK, 
                MessageBoxImage.Information);
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            this.Close();
        }

        /// <summary>
        /// Tags a list of ComplianceResults with a category name for grouped display.
        /// </summary>
        private void TagCategory(List<ComplianceResult> results, string category)
        {
            foreach (var r in results)
            {
                if (string.IsNullOrEmpty(r.Category))
                    r.Category = category;
            }
        }
    }

    /// <summary>
    /// Represents a row in the DCR Parameters grid
    /// </summary>
    public class DCRParameterRow
    {
        public string Parameter { get; set; } = string.Empty;
        public double RequiredValue { get; set; }
        public string Unit { get; set; } = string.Empty;
        public string Notes { get; set; } = string.Empty;
    }
}
