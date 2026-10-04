# API Reference

Complete API endpoint documentation for Bee Logistics Backend.

## Base URL

- **Development**: `https://localhost:5001/api`
- **Production**: `https://api.yourdomain.com/api`

## Authentication

Most endpoints require JWT Bearer authentication. Include the token in the Authorization header:

```
Authorization: Bearer <your-jwt-token>
```

## Response Format

All endpoints return standardized responses:

### Success Response
```json
{
  "success": true,
  "message": "Operation completed successfully",
  "data": { ... }
}
```

### Error Response
```json
{
  "success": false,
  "message": "Error message",
  "errors": ["Error detail 1", "Error detail 2"]
}
```

### Paginated Response
```json
{
  "success": true,
  "data": {
    "items": [...],
    "totalCount": 100,
    "page": 1,
    "pageSize": 10,
    "totalPages": 10,
    "hasNextPage": true,
    "hasPreviousPage": false
  }
}
```

---

## Authentication Endpoints

### POST /api/auth/login

Authenticate user and receive JWT token.

**Request Body**:
```json
{
  "email": "user@example.com",
  "password": "password123"
}
```

**Response**:
```json
{
  "success": true,
  "data": {
    "token": "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...",
    "expiration": "2024-01-01T12:00:00Z",
    "user": {
      "id": "user-id",
      "email": "user@example.com",
      "fullName": "John Doe",
      "role": "Admin",
      "tenantId": "company-id",
      "isOnboarded": true,
      "businessType": "Fleet",
      "isSoloDriver": false
    }
  }
}
```

**Status Codes**:
- `200 OK`: Login successful
- `401 Unauthorized`: Invalid credentials

---

### POST /api/auth/register

Register a new user.

**Request Body**:
```json
{
  "email": "user@example.com",
  "password": "password123",
  "fullName": "John Doe",
  "role": "Client",
  "companyId": "company-id" // Optional
}
```

**Response**:
```json
{
  "success": true,
  "message": "User registered successfully"
}
```

**Status Codes**:
- `200 OK`: Registration successful
- `400 Bad Request`: Email already registered or validation error

---

### GET /api/auth/me

Get current authenticated user information.

**Headers**: `Authorization: Bearer <token>`

**Response**:
```json
{
  "success": true,
  "data": {
    "id": "user-id",
    "email": "user@example.com",
    "fullName": "John Doe",
    "role": "Admin",
    "tenantId": "company-id",
    "isOnboarded": true,
    "businessType": "Fleet",
    "isSoloDriver": false
  }
}
```

---

## Bookings Endpoints

### GET /api/bookings

Get all bookings (filtered by user's tenant).

**Query Parameters**:
- `page` (int, default: 1): Page number
- `pageSize` (int, default: 10): Items per page

**Response**:
```json
{
  "success": true,
  "data": {
    "items": [
      {
        "id": "booking-id",
        "bookingNumber": "BK-20240101001",
        "customerId": "customer-id",
        "pickupLocation": "123 Main St",
        "dropoffLocation": "456 Oak Ave",
        "truckType": "Medium",
        "cargoDescription": "Furniture",
        "scheduleDate": "2024-01-15T10:00:00Z",
        "status": "Pending",
        "size": "Medium",
        "assignmentStatus": "Unassigned",
        "weightKg": 500.0,
        "pickupLatitude": 14.5995,
        "pickupLongitude": 120.9842
      }
    ],
    "totalCount": 50,
    "page": 1,
    "pageSize": 10
  }
}
```

**Booking Statuses**:
- `Pending`: Awaiting assignment
- `Assigned`: Assigned to operator
- `Broadcasting`: Broadcasting to drivers
- `Confirmed`: Driver confirmed
- `InProgress`: Pickup completed
- `Completed`: Delivery completed
- `Cancelled`: Cancelled

**Booking Sizes**:
- `Small`: Small cargo
- `Medium`: Medium cargo
- `Large`: Large cargo

**Assignment Statuses**:
- `Unassigned`: Not yet assigned
- `Assigned`: Assigned to operator
- `Broadcasting`: Broadcasting to drivers

---

### GET /api/bookings/{id}

Get booking by ID.

**Response**:
```json
{
  "success": true,
  "data": {
    "id": "booking-id",
    "bookingNumber": "BK-20240101001",
    "customerId": "customer-id",
    "pickupLocation": "123 Main St",
    "dropoffLocation": "456 Oak Ave",
    "truckType": "Medium",
    "cargoDescription": "Furniture",
    "scheduleDate": "2024-01-15T10:00:00Z",
    "status": "Pending",
    "notes": "Handle with care"
  }
}
```

---

### GET /api/bookings/customer/{customerId}

Get all bookings for a specific customer.

**Query Parameters**: Same as GET /api/bookings

---

### POST /api/bookings

Create a new booking.

**Request Body**:
```json
{
  "customerId": "customer-id",
  "pickupLocation": "123 Main St",
  "dropoffLocation": "456 Oak Ave",
  "truckType": "Medium",
  "cargoDescription": "Furniture",
  "scheduleDate": "2024-01-15T10:00:00Z",
  "notes": "Handle with care",
  "weightKg": 500.0,
  "pickupLatitude": 14.5995,
  "pickupLongitude": 120.9842
}
```

**Response**:
```json
{
  "success": true,
  "data": {
    "id": "booking-id",
    "bookingNumber": "BK-20240101001",
    ...
  }
}
```

---

### PATCH /api/bookings/{id}/status

Update booking status.

**Request Body**:
```json
{
  "status": "InProgress"
}
```

**Status Codes**:
- `200 OK`: Status updated
- `400 Bad Request`: Invalid status transition

---

### DELETE /api/bookings/{id}

Delete a booking.

**Status Codes**:
- `200 OK`: Booking deleted
- `404 Not Found`: Booking not found

---

### GET /api/bookings/pending-assignment

Get bookings pending assignment (Admin/Owner only).

**Authorization**: Requires `Admin` or `Owner` role

**Response**: Same format as GET /api/bookings

---

### GET /api/bookings/assigned-to-me

Get bookings assigned to current user's tenant.

**Response**: Same format as GET /api/bookings

---

### POST /api/bookings/{id}/classify-size

Classify booking size (Admin/Owner only).

**Request Body**:
```json
{
  "size": "Medium"
}
```

**Authorization**: Requires `Admin` or `Owner` role

---

### POST /api/bookings/{id}/assign-to-operator

Assign booking to an operator tenant (Admin/Owner only).

**Request Body**:
```json
{
  "operatorTenantId": "tenant-id"
}
```

**Authorization**: Requires `Admin` or `Owner` role

---

### POST /api/bookings/{id}/start-broadcast

Start broadcasting booking to drivers (Admin/Owner only).

**Authorization**: Requires `Admin` or `Owner` role

---

## Fleet Endpoints

### GET /api/trucks

Get all trucks for current user's company.

**Response**:
```json
{
  "success": true,
  "data": [
    {
      "id": "truck-id",
      "plateNumber": "ABC-1234",
      "type": "Medium",
      "capacity": 5000.0,
      "status": "Available",
      "companyId": "company-id",
      "currentLocation": "Manila",
      "lastMaintenanceDate": "2024-01-01T00:00:00Z",
      "driverId": "driver-id"
    }
  ]
}
```

**Truck Statuses**:
- `Available`: Ready for assignment
- `InUse`: Currently assigned to a dispatch
- `Maintenance`: Under maintenance
- `OutOfService`: Not available

---

### GET /api/trucks/{id}

Get truck by ID.

---

### POST /api/trucks

Create a new truck.

**Request Body**:
```json
{
  "plateNumber": "ABC-1234",
  "type": "Medium",
  "capacity": 5000.0,
  "currentLocation": "Manila"
}
```

**Response**:
```json
{
  "success": true,
  "data": {
    "id": "truck-id",
    "plateNumber": "ABC-1234",
    ...
  }
}
```

---

### PUT /api/trucks/{id}

Update truck information.

**Request Body**:
```json
{
  "plateNumber": "ABC-1234",
  "type": "Large",
  "capacity": 10000.0,
  "currentLocation": "Quezon City"
}
```

---

### PATCH /api/trucks/{id}/status

Update truck status.

**Request Body**:
```json
{
  "status": "Maintenance"
}
```

---

### DELETE /api/trucks/{id}

Delete a truck.

---

### POST /api/trucks/{id}/assign-driver

Assign a driver to a truck.

**Request Body**:
```json
{
  "driverId": "driver-id"
}
```

---

### POST /api/trucks/{id}/remove-driver

Remove driver assignment from truck.

---

### GET /api/trucks/available-drivers

Get list of available drivers (not assigned to any truck).

**Response**:
```json
{
  "success": true,
  "data": [
    {
      "id": "driver-id",
      "fullName": "John Driver",
      "email": "driver@example.com"
    }
  ]
}
```

---

## Operations Endpoints

### GET /api/dispatches

Get all dispatches.

**Query Parameters**:
- `page` (int, default: 1)
- `pageSize` (int, default: 10)

**Response**:
```json
{
  "success": true,
  "data": {
    "items": [
      {
        "id": "dispatch-id",
        "dispatchNumber": "DSP-1234567890",
        "bookingId": "booking-id",
        "truckId": "truck-id",
        "driverId": "driver-id",
        "status": "Pending",
        "departureTime": "2024-01-15T08:00:00Z",
        "arrivalTime": null,
        "notes": "Handle with care"
      }
    ],
    "totalCount": 20
  }
}
```

**Dispatch Statuses**:
- `Pending`: Created but not started
- `InTransit`: Currently in progress
- `Delivered`: Successfully completed
- `Cancelled`: Cancelled

---

### GET /api/dispatches/{id}

Get dispatch by ID.

---

### GET /api/dispatches/driver/{driverId}

Get dispatches for a specific driver.

---

### POST /api/dispatches

Create a new dispatch.

**Request Body**:
```json
{
  "bookingId": "booking-id",
  "truckId": "truck-id",
  "driverId": "driver-id",
  "notes": "Handle with care"
}
```

---

### PATCH /api/dispatches/{id}/status

Update dispatch status.

**Request Body**:
```json
{
  "status": "InTransit"
}
```

---

### DELETE /api/dispatches/{id}

Delete a dispatch.

---

## Payment Endpoints

### GET /api/payments

Get all payments.

**Query Parameters**:
- `page` (int, default: 1)
- `pageSize` (int, default: 10)

---

### GET /api/payments/{id}

Get payment by ID.

---

### GET /api/payments/booking/{bookingId}

Get payment for a specific booking.

---

### GET /api/payments/customer/{customerId}

Get payments for a specific customer.

---

### POST /api/payments

Create a new payment.

**Request Body**:
```json
{
  "bookingId": "booking-id",
  "amount": 5000.00,
  "currency": "PHP",
  "paymentMethod": "CreditCard"
}
```

---

### POST /api/webhooks/xendit

Xendit webhook endpoint for payment status updates.

**Note**: This endpoint is called by Xendit, not by clients.

---

## CRM Endpoints

### GET /api/tickets

Get all support tickets.

**Query Parameters**:
- `page` (int, default: 1)
- `pageSize` (int, default: 10)
- `status` (string, optional): Filter by status
- `priority` (string, optional): Filter by priority

**Response**:
```json
{
  "success": true,
  "data": {
    "items": [
      {
        "id": "ticket-id",
        "ticketNumber": "TKT-20240101001",
        "customerProfileId": "customer-id",
        "subject": "Delivery Issue",
        "description": "Package was damaged",
        "category": "Delivery",
        "priority": "High",
        "status": "Open",
        "assignedToUserId": "user-id",
        "createdAt": "2024-01-01T00:00:00Z"
      }
    ]
  }
}
```

**Ticket Categories**:
- `General`
- `Booking`
- `Payment`
- `Delivery`
- `Complaint`
- `Feedback`

**Ticket Priorities**:
- `Low`
- `Normal`
- `High`
- `Urgent`

**Ticket Statuses**:
- `Open`
- `InProgress`
- `WaitingCustomer`
- `Resolved`
- `Closed`

---

### POST /api/tickets

Create a new support ticket.

**Request Body**:
```json
{
  "customerProfileId": "customer-id",
  "subject": "Delivery Issue",
  "description": "Package was damaged",
  "category": "Delivery",
  "priority": "High",
  "bookingId": "booking-id" // Optional
}
```

---

### GET /api/faq

Get all FAQ articles.

**Query Parameters**:
- `category` (string, optional): Filter by category

**Response**:
```json
{
  "success": true,
  "data": [
    {
      "id": "faq-id",
      "title": "How do I book a truck?",
      "content": "To book a truck...",
      "category": "Booking",
      "tags": "booking,truck,how-to",
      "isPublished": true,
      "sortOrder": 1
    }
  ]
}
```

---

## Chat Endpoints

### GET /api/chat/conversations

Get all conversations for current user.

**Response**:
```json
{
  "success": true,
  "data": [
    {
      "id": "conversation-id",
      "title": "Chat with John",
      "type": "Direct",
      "bookingId": null,
      "dispatchId": null,
      "lastMessageAt": "2024-01-01T12:00:00Z",
      "isActive": true
    }
  ]
}
```

**Conversation Types**:
- `Direct`: 1:1 chat
- `Group`: Group chat
- `Support`: Customer support
- `Dispatch`: Dispatch-related
- `Business`: Customer ↔ Fleet

---

### POST /api/chat/conversations

Create a new conversation.

**Request Body**:
```json
{
  "title": "Chat with John",
  "type": "Direct",
  "participantIds": ["user-id-1", "user-id-2"],
  "bookingId": null, // Optional
  "dispatchId": null // Optional
}
```

---

### GET /api/chat/conversations/{id}/messages

Get messages for a conversation.

**Query Parameters**:
- `page` (int, default: 1)
- `pageSize` (int, default: 50)

**Response**:
```json
{
  "success": true,
  "data": {
    "items": [
      {
        "id": "message-id",
        "conversationId": "conversation-id",
        "senderId": "user-id",
        "senderName": "John Doe",
        "content": "Hello!",
        "messageType": "Text",
        "createdAt": "2024-01-01T12:00:00Z"
      }
    ]
  }
}
```

---

## Map Endpoints

### GET /api/locations/driver/{driverId}

Get current location for a driver.

**Response**:
```json
{
  "success": true,
  "data": {
    "driverId": "driver-id",
    "latitude": 14.5995,
    "longitude": 120.9842,
    "updatedAt": "2024-01-01T12:00:00Z"
  }
}
```

---

### POST /api/locations/driver/{driverId}

Update driver location.

**Request Body**:
```json
{
  "latitude": 14.5995,
  "longitude": 120.9842
}
```

---

## Company Endpoints

### GET /api/companies

Get all companies.

---

### GET /api/companies/{id}

Get company by ID.

---

### POST /api/companies

Create a new company.

**Request Body**:
```json
{
  "name": "ABC Logistics",
  "address": "123 Main St",
  "contactNumber": "+63 123 456 7890",
  "email": "contact@abclogistics.com"
}
```

---

## Driver Applications Endpoints

### GET /api/driver-applications

Get all driver applications.

---

### POST /api/driver-applications

Submit a driver application.

**Request Body**:
```json
{
  "fullName": "John Driver",
  "email": "driver@example.com",
  "phone": "+63 123 456 7890",
  "licenseNumber": "DL-123456",
  "experienceYears": 5
}
```

---

## SignalR Hubs

### /hubs/notifications

Real-time notification hub.

**Connection**:
```javascript
const connection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/notifications", {
    accessTokenFactory: () => token
  })
  .build();

connection.on("NotificationReceived", (notification) => {
  console.log(notification);
});
```

---

### /hubs/chat

Real-time chat hub.

**Connection**:
```javascript
const connection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/chat", {
    accessTokenFactory: () => token
  })
  .build();

connection.on("MessageReceived", (message) => {
  console.log(message);
});

connection.invoke("SendMessage", conversationId, content);
```

---

## Error Codes

### HTTP Status Codes

- `200 OK`: Request successful
- `201 Created`: Resource created successfully
- `400 Bad Request`: Invalid request data
- `401 Unauthorized`: Authentication required
- `403 Forbidden`: Insufficient permissions
- `404 Not Found`: Resource not found
- `500 Internal Server Error`: Server error

### Common Error Responses

**Validation Error**:
```json
{
  "success": false,
  "message": "Validation failed",
  "errors": [
    "Email is required",
    "Password must be at least 8 characters"
  ]
}
```

**Not Found**:
```json
{
  "success": false,
  "message": "Resource not found"
}
```

**Unauthorized**:
```json
{
  "success": false,
  "message": "Access denied"
}
```

---

## Rate Limiting

Currently, no rate limiting is implemented. Consider implementing rate limiting for production use.

---

## Versioning

API versioning is not currently implemented. Future versions may include versioning via URL path (`/api/v1/...`) or headers.

