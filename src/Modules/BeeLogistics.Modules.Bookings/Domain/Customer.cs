using System;
using System.Collections.Generic;
using BeeLogistics.Shared.Abstractions;

namespace BeeLogistics.Modules.Bookings.Domain;

public class Customer : Entity
{
    public string Name { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string? PasswordHash { get; private set; }
    public string CompanyName { get; private set; } = string.Empty;
    public string Phone { get; private set; } = string.Empty;
    public string Address { get; private set; } = string.Empty;
    public bool IsActive { get; private set; } = true;

    // Navigation property for EF Core
    private readonly List<Booking> _bookings = new();
    public IReadOnlyCollection<Booking> Bookings => _bookings.AsReadOnly();

    private Customer() { } // For EF

    public Customer(string name, string email, string companyName, string phone, string address, string? passwordHash = null)
    {
        Name = name;
        Email = email;
        CompanyName = companyName;
        Phone = phone;
        Address = address;
        PasswordHash = passwordHash;
        IsActive = true;
    }

    public void UpdateProfile(string name, string companyName, string phone, string address)
    {
        Name = name;
        CompanyName = companyName;
        Phone = phone;
        Address = address;
        UpdatedAt = DateTime.UtcNow;
    }

    public void SetPasswordHash(string passwordHash)
    {
        PasswordHash = passwordHash;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Deactivate()
    {
        IsActive = false;
        UpdatedAt = DateTime.UtcNow;
    }

    public void Activate()
    {
        IsActive = true;
        UpdatedAt = DateTime.UtcNow;
    }
}
