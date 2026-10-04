using System;
using System.Collections.Generic;
using System.Linq;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Bookings.Domain;

public class Booking : Entity
{
    public string BookingNumber { get; private set; } = null!;
    public Guid CustomerId { get; private set; }
    
    // Legacy fields (deprecated - kept for backward compatibility)
    [Obsolete("Use Stops collection instead. This will be removed in a future version.")]
    public string PickupLocation { get; private set; } = null!;
    
    [Obsolete("Use Stops collection instead. This will be removed in a future version.")]
    public string DropoffLocation { get; private set; } = null!;
    
    public string VehicleType { get; private set; } = null!; // Renamed from TruckType
    public string CargoDescription { get; private set; } = null!;
    public DateTime ScheduleDate { get; private set; }
    public BookingStatus Status { get; private set; }
    public string? Notes { get; private set; }
    
    // Multi-stop support
    private readonly List<DeliveryStop> _stops = new();
    public IReadOnlyCollection<DeliveryStop> Stops => _stops.AsReadOnly();
    
    // Pricing
    public decimal EstimatedFare { get; private set; }
    public decimal? FinalFare { get; private set; }
    public decimal? DistanceKm { get; private set; }
    public decimal? PriorityFee { get; private set; }
    
    // Service type
    public ServiceType ServiceType { get; private set; }

    /// <summary>
    /// What the customer bought beyond the delivery itself. Orthogonal to <see cref="ServiceType"/>.
    /// </summary>
    public DeliveryMode DeliveryMode { get; private set; }

    /// <summary>
    /// Cross-booking dispatch rank, higher served first. Derived from <see cref="DeliveryMode"/>
    /// and set only in a constructor, so the two cannot drift.
    /// </summary>
    /// <remarks>
    /// Persisted as an int rather than ordering on DeliveryMode directly because enums are stored
    /// as strings here (see BookingsDbContext) — ORDER BY "DeliveryMode" would sort alphabetically,
    /// giving OnDemand &lt; Pooling &lt; Regular, i.e. exactly wrong. A CASE expression translates
    /// but cannot be indexed and would duplicate the ranking across every query that needs it.
    ///
    /// Nothing reads this yet; the dispatch ordering that uses it lands separately.
    /// </remarks>
    public int DispatchPriority { get; private set; }
    public DateTime? ScheduledDateTime { get; private set; }
    public string? ScheduledPickupWindow { get; private set; }
    
    // Driver assignment (driver-broadcast model)
    public Guid? SelectedDriverId { get; private set; }
    public Guid? FavouriteDriverId { get; private set; }
    public DateTime? DriverAssignedAt { get; private set; }
    
    // Cargo details
    public decimal? WeightKg { get; private set; }
    public string? ItemImagePath { get; private set; }
    /// <summary>Item dimensions L×W×H in cm (optional).</summary>
    public decimal? ItemLengthCm { get; private set; }
    public decimal? ItemWidthCm { get; private set; }
    public decimal? ItemHeightCm { get; private set; }
    
    // Cancellation details
    public string? CancellationReason { get; private set; }
    public Guid? CancelledBy { get; private set; }
    public DateTime? CancelledAt { get; private set; }
    
    // Legacy fields (deprecated - kept for backward compatibility during migration)
    [Obsolete("BookingSize is deprecated. This will be removed in a future version.")]
    public BookingSize? Size { get; private set; }
    
    [Obsolete("BookingAssignmentStatus is deprecated. Use Status instead. This will be removed in a future version.")]
    public BookingAssignmentStatus AssignmentStatus { get; private set; }

    /// <summary>
    /// When this booking is next due for a driver-availability pulse while
    /// BroadcastingToDrivers. Drives the due-only query in
    /// <see cref="Application.Interfaces.IBookingRepository.GetDueForPulseAsync"/>
    /// so the recurring pulse job only reprocesses bookings that actually need it.
    /// </summary>
    public DateTime? NextPulseAt { get; private set; }

    /// <summary>Number of times this booking has been pulsed for newly-available drivers.</summary>
    public int PulseCount { get; private set; }

    [Obsolete("AssignedAt is deprecated. Use DriverAssignedAt instead. This will be removed in a future version.")]
    public DateTime? AssignedAt { get; private set; }

    [Obsolete("Use Stops collection instead. This will be removed in a future version.")]
    public decimal? PickupLatitude { get; private set; }
    
    [Obsolete("Use Stops collection instead. This will be removed in a future version.")]
    public decimal? PickupLongitude { get; private set; }
    
    [Obsolete("Use Stops collection instead. This will be removed in a future version.")]
    public decimal? DropoffLatitude { get; private set; }
    
    [Obsolete("Use Stops collection instead. This will be removed in a future version.")]
    public decimal? DropoffLongitude { get; private set; }

    // Navigation properties
    public Customer Customer { get; private set; } = null!;
    private readonly List<ProofOfDelivery> _proofOfDeliveries = new();
    public IReadOnlyCollection<ProofOfDelivery> ProofOfDeliveries => _proofOfDeliveries.AsReadOnly();

    /// <summary>
    /// Optimistic concurrency token (PostgreSQL xmin). Guards the driver-accept
    /// race: two simultaneous accepts both pass the status check, but only the
    /// first save wins — the second gets DbUpdateConcurrencyException.
    /// </summary>
    public uint Version { get; private set; }

    private Booking() { }

    // Constructor for a booking: exactly one pickup and one dropoff
    public Booking(
        Guid customerId,
        string vehicleType,
        string cargoDescription,
        DateTime scheduleDate,
        ServiceType serviceType,
        decimal estimatedFare,
        List<DeliveryStop> stops,
        decimal? weightKg = null,
        decimal? priorityFee = null,
        DateTime? scheduledDateTime = null,
        string? scheduledPickupWindow = null,
        Guid? favouriteDriverId = null,
        string? itemImagePath = null,
        decimal? itemLengthCm = null,
        decimal? itemWidthCm = null,
        decimal? itemHeightCm = null,
        string? notes = null,
        DeliveryMode deliveryMode = DeliveryMode.Regular)
    {
        Id = Guid.NewGuid();
        if (stops == null || stops.Count == 0)
            throw new ArgumentException("A pickup and a dropoff stop are required", nameof(stops));

        // A booking is exactly one pickup and one dropoff. Multi-stop within a single booking is
        // deliberately not supported: the platform models multiple deliveries as multiple
        // independent bookings, and a driver may hold several at once (DriverCapacityPolicy caps
        // that at 3). Widening this back to N stops would also silently resurrect the multi-stop
        // fee that PricingService no longer charges.
        if (stops.Count != 2)
            throw new ArgumentException(
                "A booking must have exactly two stops: one pickup and one dropoff", nameof(stops));

        if (stops.Count(s => s.Type == StopType.Pickup) != 1)
            throw new ArgumentException("Exactly one pickup stop is required", nameof(stops));

        if (stops.Count(s => s.Type == StopType.Dropoff) != 1)
            throw new ArgumentException("Exactly one dropoff stop is required", nameof(stops));

        CustomerId = customerId;
        VehicleType = vehicleType;
        CargoDescription = cargoDescription;
        ScheduleDate = scheduleDate;
        ServiceType = serviceType;
        DeliveryMode = deliveryMode;
        DispatchPriority = DeliveryModeRanking.DispatchPriorityOf(deliveryMode);
        EstimatedFare = estimatedFare;
        WeightKg = weightKg;
        PriorityFee = priorityFee;
        ScheduledDateTime = scheduledDateTime;
        ScheduledPickupWindow = scheduledPickupWindow;
        FavouriteDriverId = favouriteDriverId;
        ItemImagePath = itemImagePath;
        ItemLengthCm = itemLengthCm;
        ItemWidthCm = itemWidthCm;
        ItemHeightCm = itemHeightCm;
        Notes = notes;
        
        Status = BookingStatus.Pending;
        BookingNumber = GenerateBookingNumber();
        
        // Add stops
        foreach (var stop in stops.OrderBy(s => s.Sequence))
        {
            _stops.Add(stop);
        }
        
        // Set legacy fields for backward compatibility
        var pickupStop = stops.First(s => s.Type == StopType.Pickup);
        PickupLocation = pickupStop.Address;
        PickupLatitude = pickupStop.Latitude;
        PickupLongitude = pickupStop.Longitude;
        
        var firstDropoff = stops.FirstOrDefault(s => s.Type == StopType.Dropoff);
        if (firstDropoff != null)
        {
            DropoffLocation = firstDropoff.Address;
            DropoffLatitude = firstDropoff.Latitude;
            DropoffLongitude = firstDropoff.Longitude;
        }
        else
        {
            DropoffLocation = pickupStop.Address; // Fallback
        }
        
        // Legacy assignment status
        AssignmentStatus = BookingAssignmentStatus.PendingAssignment;
    }
    
    // Legacy constructor (kept for backward compatibility)
    [Obsolete("Use the multi-stop constructor instead. This will be removed in a future version.")]
    public Booking(
        Guid customerId, 
        string pickupLocation, 
        string dropoffLocation, 
        string truckType, 
        string cargoDescription, 
        DateTime scheduleDate,
        decimal? weightKg = null,
        decimal? pickupLatitude = null,
        decimal? pickupLongitude = null,
        decimal? dropoffLatitude = null,
        decimal? dropoffLongitude = null,
        string? itemImagePath = null,
        decimal? itemLengthCm = null,
        decimal? itemWidthCm = null,
        decimal? itemHeightCm = null)
    {
        Id = Guid.NewGuid();
        CustomerId = customerId;
        PickupLocation = pickupLocation;
        DropoffLocation = dropoffLocation;
        VehicleType = truckType;
        CargoDescription = cargoDescription;
        ScheduleDate = scheduleDate;
        WeightKg = weightKg;
        PickupLatitude = pickupLatitude;
        PickupLongitude = pickupLongitude;
        DropoffLatitude = dropoffLatitude;
        DropoffLongitude = dropoffLongitude;
        ItemImagePath = itemImagePath;
        ItemLengthCm = itemLengthCm;
        ItemWidthCm = itemWidthCm;
        ItemHeightCm = itemHeightCm;
        
        Status = BookingStatus.Pending;
        ServiceType = ServiceType.Immediate;
        // Explicit, not left to default(int): 0 is not the Regular rank, and a 0 here would
        // sort every legacy booking behind Pooling once dispatch ordering lands.
        DeliveryMode = DeliveryMode.Regular;
        DispatchPriority = DeliveryModeRanking.DispatchPriorityOf(DeliveryMode.Regular);
        AssignmentStatus = BookingAssignmentStatus.PendingAssignment;
        BookingNumber = GenerateBookingNumber();
        
        // Create stops from legacy fields
        var pickupStop = new DeliveryStop(Id, 0, pickupLocation, StopType.Pickup, pickupLatitude, pickupLongitude);
        _stops.Add(pickupStop);
        
        var dropoffStop = new DeliveryStop(Id, 1, dropoffLocation, StopType.Dropoff, dropoffLatitude, dropoffLongitude);
        _stops.Add(dropoffStop);
        
        // Auto-classify if weight < 100kg
        if (weightKg.HasValue && weightKg.Value < 100)
        {
            Size = BookingSize.Small;
        }
    }
    
    private string GenerateBookingNumber()
    {
        return $"BKG-{DateTime.UtcNow:yyyyMMdd}-{DateTime.UtcNow.Ticks % 1000000:D6}";
    }

    // Status transitions for the multi-stop, driver-broadcast model
    public void ConfirmDriver(Guid driverId)
    {
        if (Status != BookingStatus.Pending)
            throw new InvalidOperationException($"Cannot confirm driver. Current status: {Status}");

        SelectedDriverId = driverId;
        DriverAssignedAt = DateTime.UtcNow;
        Status = BookingStatus.Confirmed;
        UpdatedAt = DateTime.UtcNow;
        
        // Legacy fields for backward compatibility
        AssignmentStatus = BookingAssignmentStatus.AcceptedByDriver;
        AssignedAt = DriverAssignedAt;
    }

    public void MarkDriverEnRoute()
    {
        if (Status == BookingStatus.DriverAssigned) return; // Idempotent
        if (Status != BookingStatus.Confirmed)
            throw new InvalidOperationException($"Cannot mark driver en route. Current status: {Status}");

        Status = BookingStatus.DriverAssigned;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkPickedUp()
    {
        if (Status == BookingStatus.PickedUp) return; // Idempotent
        if (Status != BookingStatus.DriverAssigned && Status != BookingStatus.Confirmed)
            throw new InvalidOperationException($"Cannot mark as picked up. Current status: {Status}");

        Status = BookingStatus.PickedUp;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkInTransit()
    {
        if (Status == BookingStatus.InTransit) return; // Idempotent
        if (Status != BookingStatus.PickedUp)
            throw new InvalidOperationException($"Cannot mark in transit. Current status: {Status}");

        Status = BookingStatus.InTransit;
        UpdatedAt = DateTime.UtcNow;
    }

    public void MarkCompleted()
    {
        if (Status != BookingStatus.InTransit && Status != BookingStatus.PickedUp)
            throw new InvalidOperationException($"Cannot mark as completed. Current status: {Status}");

        // Check if all stops are completed
        var incompleteStops = _stops.Where(s => s.Status != StopStatus.Completed).ToList();
        if (incompleteStops.Any())
            throw new InvalidOperationException($"Cannot complete booking. {incompleteStops.Count} stops are not completed");

        Status = BookingStatus.Completed;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Cancel(string reason, Guid? cancelledBy = null)
    {
        if (Status == BookingStatus.Completed)
            throw new InvalidOperationException("Cannot cancel a completed booking");

        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Cancellation reason is required", nameof(reason));

        Status = BookingStatus.Cancelled;
        CancellationReason = reason;
        CancelledBy = cancelledBy;
        CancelledAt = DateTime.UtcNow;
        Notes = !string.IsNullOrWhiteSpace(Notes) ? $"{Notes}\nCancelled: {reason}".Trim() : $"Cancelled: {reason}";
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetFinalFare(decimal finalFare)
    {
        if (finalFare < 0)
            throw new ArgumentException("Final fare cannot be negative", nameof(finalFare));

        FinalFare = finalFare;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetDistance(decimal distanceKm)
    {
        if (distanceKm < 0)
            throw new ArgumentException("Distance cannot be negative", nameof(distanceKm));

        DistanceKm = distanceKm;
        UpdatedAt = DateTime.UtcNow;
    }

    // AddStop was removed with multi-stop support: a booking's two stops are fixed at construction
    // and there is no longer a legal way to add a third. It had no callers.

    public void AddProofOfDelivery(ProofOfDelivery pod)
    {
        _proofOfDeliveries.Add(pod);
        UpdatedAt = DateTime.UtcNow;
    }

    // Legacy methods (kept for backward compatibility)
    [Obsolete("Use ConfirmDriver instead. This will be removed in a future version.")]
    public void MarkAsDispatched()
    {
        Status = BookingStatus.DriverAssigned;
        UpdatedAt = DateTime.UtcNow;
    }

    [Obsolete("Use MarkDriverEnRoute instead. This will be removed in a future version.")]
    public void MarkAsOnTheWayToPickup()
    {
        Status = BookingStatus.DriverAssigned;
        UpdatedAt = DateTime.UtcNow;
    }

    [Obsolete("Use MarkInTransit instead. This will be removed in a future version.")]
    public void MarkAsInProgress()
    {
        Status = BookingStatus.InTransit;
        UpdatedAt = DateTime.UtcNow;
    }

    [Obsolete("Use MarkCompleted instead. This will be removed in a future version.")]
    public void MarkAsDelivered()
    {
        Status = BookingStatus.Completed;
        UpdatedAt = DateTime.UtcNow;
    }

    [Obsolete("Use MarkCompleted instead. This will be removed in a future version.")]
    public void MarkAsCompleted()
    {
        MarkCompleted();
    }

    [Obsolete("BookingSize classification is deprecated. This will be removed in a future version.")]
    public void ClassifySize(BookingSize size)
    {
        Size = size;
        UpdatedAt = DateTime.UtcNow;
    }

    [Obsolete("Broadcasting is deprecated. Use driver matching instead. This will be removed in a future version.")]
    public void StartBroadcastingToDrivers()
    {
        AssignmentStatus = BookingAssignmentStatus.BroadcastingToDrivers;
        NextPulseAt = DateTime.UtcNow;
        UpdatedAt = DateTime.UtcNow;
    }

    /// <summary>
    /// Records that this booking was just pulsed for newly-available drivers and
    /// schedules the next due time, so the due-only pulse query skips it until then.
    /// </summary>
    public void SchedulePulse(DateTime nextPulseAt)
    {
        NextPulseAt = nextPulseAt;
        PulseCount++;
        UpdatedAt = DateTime.UtcNow;
    }

    [Obsolete("Use ConfirmDriver instead. This will be removed in a future version.")]
    public void AcceptByDriver(Guid driverId)
    {
        ConfirmDriver(driverId);
    }

    /// <summary>The cancellation reason recorded when the broadcast window expires unaccepted.</summary>
    /// <remarks>
    /// Matches <c>CancellationReason.NoDriverFound</c> on the manual cancel path, so customers
    /// see one wording however the booking ended up cancelled.
    /// </remarks>
    public const string NoDriverFoundReason = "No driver found";

    /// <summary>
    /// Gives up on finding a driver: the broadcast window expired with no acceptance.
    /// </summary>
    /// <remarks>
    /// Moves <see cref="Status"/> as well as <see cref="AssignmentStatus"/>. Until this was
    /// fixed only the latter moved, so a booking nobody accepted read as Pending forever and
    /// the customer's app showed it still searching.
    ///
    /// Cancelled is reused rather than adding a BookingStatus value: deployed mobile builds
    /// already handle Cancelled, and would render a new status as unknown.
    ///
    /// Idempotent, and a no-op once a driver has accepted - the sweep can reach the same
    /// booking twice, and an acceptance can land between the caller's check and this call.
    /// </remarks>
    public void MarkAsRejectedByAllDrivers()
    {
        if (Status != BookingStatus.Pending)
            return;

        AssignmentStatus = BookingAssignmentStatus.RejectedByAllDrivers;
        Cancel(NoDriverFoundReason);
    }

    public void SetItemImagePath(string? imagePath)
    {
        ItemImagePath = imagePath;
        UpdatedAt = DateTime.UtcNow;
    }
}

public enum BookingStatus
{
    Pending,           // Customer created, waiting for driver acceptance
    Confirmed,         // Driver accepted
    DriverAssigned,    // Driver en route to pickup
    PickedUp,          // Driver picked up items
    InTransit,        // Driver delivering
    Completed,        // All stops completed
    Cancelled,        // Cancelled by customer or driver
    
    // Legacy statuses (deprecated - kept for backward compatibility)
    [Obsolete("Use DriverAssigned instead. This will be removed in a future version.")]
    Dispatched = DriverAssigned,
    
    [Obsolete("Use DriverAssigned instead. This will be removed in a future version.")]
    OnTheWayToPickup = DriverAssigned,
    
    [Obsolete("Use InTransit instead. This will be removed in a future version.")]
    InProgress = InTransit,
    
    [Obsolete("Use Completed instead. This will be removed in a future version.")]
    Delivered = Completed
}

public enum ServiceType
{
    Immediate,   // On-demand delivery
    Scheduled    // Scheduled delivery (up to 30 days ahead)
}

/// <summary>
/// What the customer bought beyond the delivery itself.
/// </summary>
/// <remarks>
/// Orthogonal to <see cref="ServiceType"/>, which says only <i>when</i>. All six combinations are
/// valid: a Scheduled OnDemand booking is "reserve 3pm, and be aggressive about filling it".
///
/// Pooling does NOT mean a merged trip. A pooled booking is still one independent booking with one
/// pickup, one dropoff and its own fare; the mode only biases which drivers are offered it first.
///
/// Regular is ordinal 0 so a default-constructed value is the harmless one.
/// </remarks>
public enum DeliveryMode
{
    /// <summary>Baseline fare, baseline dispatch. What a booking is unless told otherwise.</summary>
    Regular,

    /// <summary>Discounted, and patient: waits longer for a driver already heading that way.</summary>
    Pooling,

    /// <summary>Premium fare, dispatched first and searched for harder. Named for the product.</summary>
    OnDemand,
}

// Legacy enums (deprecated - kept for backward compatibility)
[Obsolete("BookingSize is deprecated. This will be removed in a future version.")]
public enum BookingSize
{
    Small,
    Large,
    Heavy
}

[Obsolete("BookingAssignmentStatus is deprecated. Use BookingStatus instead. This will be removed in a future version.")]
public enum BookingAssignmentStatus
{
    PendingAssignment,
    AssignedToOperator,
    BroadcastingToDrivers,
    AcceptedByDriver,
    RejectedByAllDrivers
}
