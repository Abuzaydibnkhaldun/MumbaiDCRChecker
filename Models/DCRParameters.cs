using System.ComponentModel;

namespace MumbaiDCRChecker.Models
{
    /// <summary>
    /// Contains all DCR parameters for compliance checking
    /// </summary>
    public class DCRParameters : INotifyPropertyChanged
    {
        // Project Information
        public string ZoneType { get; set; } = "City"; // "City" or "Suburb"
        public string ProjectType { get; set; } = "Residential"; // "Residential", "Commercial", "Mixed", "Industrial"
        
        // Road Widths (in meters) — External roads along plot boundary
        public double NorthRoadWidth { get; set; }
        public double SouthRoadWidth { get; set; }
        public double EastRoadWidth { get; set; }
        public double WestRoadWidth { get; set; }

        // Internal Road Widths (in meters) — Roads within the layout/development
        // If an internal road exists on any side, its road-width-based setback
        // is computed and the governing (maximum) setback applies.
        public double InternalRoadWidthFront { get; set; }
        public double InternalRoadWidthRear { get; set; }
        public double InternalRoadWidthSide1 { get; set; }
        public double InternalRoadWidthSide2 { get; set; }
        
        // Level Information
        public string GroundLevelName { get; set; } = string.Empty;
        public string TopLevelName { get; set; } = string.Empty;

        // Phase 4: Fire Safety Parameters
        /// <summary>
        /// Whether the building has automatic sprinkler system.
        /// Affects travel distance limits (+50% per DCPR 2034 Reg 47(3)(ii)).
        /// Set via UI checkbox on Model Setup tab.
        /// </summary>
        public bool HasSprinklers { get; set; }

        /// <summary>
        /// Building occupancy type for fire safety threshold lookups.
        /// Examples: "Residential", "Residential - Hotel", "Educational", 
        ///           "Institutional - Hospital", "Assembly", "Commercial / Business",
        ///           "Mercantile", "Industrial", "Storage", "Hazardous"
        /// Set via UI dropdown on Model Setup tab.
        /// </summary>
        public string Occupancy { get; set; } = "Residential";

        // DCR Requirements (backing fields for property change notification)
        private double _frontSetback;
        private double _rearSetback;
        private double _sideSetback1;
        private double _sideSetback2;
        private double _maxBuildingHeight;
        private double _groundCoveragePercent;
        private int _parkingSpacesRequired;
        private double _openSpacePercent;
        private double _minInterBuildingDistance;

        public double FrontSetback
        {
            get => _frontSetback;
            set { _frontSetback = value; OnPropertyChanged(nameof(FrontSetback)); }
        }

        public double RearSetback
        {
            get => _rearSetback;
            set { _rearSetback = value; OnPropertyChanged(nameof(RearSetback)); }
        }

        public double SideSetback1
        {
            get => _sideSetback1;
            set { _sideSetback1 = value; OnPropertyChanged(nameof(SideSetback1)); }
        }

        public double SideSetback2
        {
            get => _sideSetback2;
            set { _sideSetback2 = value; OnPropertyChanged(nameof(SideSetback2)); }
        }

        public double MaxBuildingHeight
        {
            get => _maxBuildingHeight;
            set { _maxBuildingHeight = value; OnPropertyChanged(nameof(MaxBuildingHeight)); }
        }

        public double GroundCoveragePercent
        {
            get => _groundCoveragePercent;
            set { _groundCoveragePercent = value; OnPropertyChanged(nameof(GroundCoveragePercent)); }
        }

        public int ParkingSpacesRequired
        {
            get => _parkingSpacesRequired;
            set { _parkingSpacesRequired = value; OnPropertyChanged(nameof(ParkingSpacesRequired)); }
        }

        public double OpenSpacePercent
        {
            get => _openSpacePercent;
            set { _openSpacePercent = value; OnPropertyChanged(nameof(OpenSpacePercent)); }
        }

        public double MinInterBuildingDistance
        {
            get => _minInterBuildingDistance;
            set { _minInterBuildingDistance = value; OnPropertyChanged(nameof(MinInterBuildingDistance)); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        
        protected void OnPropertyChanged(string propertyName)
        {
            if (PropertyChanged != null)
            {
                PropertyChanged.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }
        }
    }

    /// <summary>
    /// Represents the result of a single compliance check
    /// </summary>
    public class ComplianceResult
    {
        public string Category { get; set; } = string.Empty;
        public string Parameter { get; set; } = string.Empty;
        public string Required { get; set; } = string.Empty;
        public string Actual { get; set; } = string.Empty;
        public string Status { get; set; } = "PENDING"; // "PASS", "FAIL", "WARNING", "PENDING"
        public string Details { get; set; } = string.Empty;
        public Autodesk.Revit.DB.ElementId[] ViolatingElementIds { get; set; }
    }
}
