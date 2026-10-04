# SignalR Integration Guide

This document provides details on SignalR hubs and how to integrate with them for real-time communication.

## SignalR Hub Endpoints

The application exposes two SignalR hubs:

### 1. Notification Hub
**URL**: `/hubs/notifications`

**Purpose**: Real-time notifications for users, groups, or all clients.

### 2. Chat Hub
**URL**: `/hubs/chat`

**Purpose**: Real-time chat messaging between users.

---

## Connection URLs

### Base URLs

- **Development**: 
  - `wss://localhost:5001/hubs/notifications`
  - `wss://localhost:5001/hubs/chat`
  
- **Production**: 
  - `wss://api.yourdomain.com/hubs/notifications`
  - `wss://api.yourdomain.com/hubs/chat`

### Authentication

SignalR hubs require JWT authentication. Pass the token as a query parameter:

```
wss://api.yourdomain.com/hubs/notifications?access_token=<your-jwt-token>
wss://api.yourdomain.com/hubs/chat?access_token=<your-jwt-token>
```

The JWT token is automatically extracted from the `access_token` query parameter for paths starting with `/hubs`.

---

## Notification Hub (`/hubs/notifications`)

### Connection

```javascript
const connection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/notifications", {
    accessTokenFactory: () => {
      // Return your JWT token
      return localStorage.getItem("token");
    }
  })
  .withAutomaticReconnect()
  .build();

await connection.start();
```

### Hub Methods (Server → Client)

The server can send notifications using these methods (via `INotificationService`):

#### Send to Specific User
```csharp
await notificationService.SendToUserAsync(userId, "NotificationReceived", data);
```

#### Send to Group
```csharp
await notificationService.SendToGroupAsync("dispatch-123", "DispatchUpdated", data);
```

#### Send to All
```csharp
await notificationService.SendToAllAsync("SystemAnnouncement", data);
```

### Client-Side Methods

#### Join a Group
```javascript
await connection.invoke("JoinGroup", "dispatch-123");
```

#### Leave a Group
```javascript
await connection.invoke("LeaveGroup", "dispatch-123");
```

### Listening for Notifications

```javascript
// Listen for any notification
connection.on("NotificationReceived", (data) => {
  console.log("Notification:", data);
});

// Listen for dispatch updates
connection.on("DispatchUpdated", (data) => {
  console.log("Dispatch updated:", data);
});

// Listen for booking updates
connection.on("BookingStatusChanged", (data) => {
  console.log("Booking status:", data);
});

// Listen for system announcements
connection.on("SystemAnnouncement", (data) => {
  console.log("Announcement:", data);
});
```

### Automatic Group Management

When a user connects, they are automatically added to:
- `user-{userId}` - Personal notification group

You can send notifications to a specific user by targeting the `user-{userId}` group.

---

## Chat Hub (`/hubs/chat`)

### Connection

```javascript
const connection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/chat", {
    accessTokenFactory: () => {
      return localStorage.getItem("token");
    }
  })
  .withAutomaticReconnect()
  .build();

await connection.start();
```

### Hub Methods (Client → Server)

#### Join a Conversation
```javascript
await connection.invoke("JoinConversation", conversationId);
```

#### Leave a Conversation
```javascript
await connection.invoke("LeaveConversation", conversationId);
```

#### Send a Message
```javascript
await connection.invoke("SendMessage", conversationId, "Hello, world!");
```

#### Mark Conversation as Read
```javascript
await connection.invoke("MarkAsRead", conversationId);
```

#### Start Typing Indicator
```javascript
await connection.invoke("StartTyping", conversationId);
```

#### Stop Typing Indicator
```javascript
await connection.invoke("StopTyping", conversationId);
```

### Hub Methods (Server → Client)

#### Receive Message
```javascript
connection.on("ReceiveMessage", (message) => {
  console.log("New message:", message);
  // message structure:
  // {
  //   id: "message-id",
  //   conversationId: "conversation-id",
  //   senderId: "user-id",
  //   senderName: "John Doe",
  //   content: "Hello!",
  //   messageType: "Text",
  //   createdAt: "2024-01-01T12:00:00Z"
  // }
});
```

#### User Typing
```javascript
connection.on("UserTyping", (data) => {
  console.log("User is typing:", data);
  // data structure:
  // {
  //   conversationId: "conversation-id",
  //   userId: "user-id",
  //   userName: "John Doe"
  // }
});
```

#### User Stopped Typing
```javascript
connection.on("UserStoppedTyping", (data) => {
  console.log("User stopped typing:", data);
  // data structure:
  // {
  //   conversationId: "conversation-id",
  //   userId: "user-id"
  // }
});
```

### Automatic Group Management

When a user connects, they are automatically added to:
- `user-{userId}` - Personal group for direct messages
- `chat-{conversationId}` - For each conversation they're part of

---

## REST API Endpoints That Trigger SignalR Events

While SignalR is primarily for real-time communication, some REST API endpoints may trigger SignalR notifications. These are typically handled internally via the `INotificationService`.

### Example: Sending Notifications from REST Endpoints

If you want to send a notification when a booking status changes, you would inject `INotificationService` in your handler:

```csharp
public class UpdateBookingStatusHandler : IRequestHandler<UpdateBookingStatusCommand, Result>
{
    private readonly INotificationService _notificationService;
    
    public async Task<Result> Handle(UpdateBookingStatusCommand request, CancellationToken ct)
    {
        // Update booking status...
        
        // Send notification to customer
        await _notificationService.SendToUserAsync(
            customerId, 
            "BookingStatusChanged", 
            new { bookingId = request.BookingId, status = newStatus }
        );
        
        return Result.Success();
    }
}
```

---

## Integration Examples

### Example 1: Real-time Booking Updates

**Server-side (in a handler or controller):**
```csharp
public class BookingController : BaseController
{
    private readonly INotificationService _notificationService;
    
    [HttpPatch("{id}/status")]
    public async Task<IActionResult> UpdateStatus(Guid id, [FromBody] UpdateStatusDto dto)
    {
        // Update booking...
        
        // Notify customer
        await _notificationService.SendToUserAsync(
            booking.CustomerId.ToString(),
            "BookingStatusChanged",
            new { bookingId = id, status = dto.Status }
        );
        
        // Notify dispatchers
        await _notificationService.SendToGroupAsync(
            "dispatchers",
            "BookingUpdated",
            new { bookingId = id, status = dto.Status }
        );
        
        return Ok();
    }
}
```

**Client-side:**
```javascript
// Connect to notification hub
const notificationConnection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/notifications", {
    accessTokenFactory: () => token
  })
  .build();

await notificationConnection.start();

// Listen for booking updates
notificationConnection.on("BookingStatusChanged", (data) => {
  updateBookingUI(data.bookingId, data.status);
});
```

### Example 2: Real-time Dispatch Tracking

**Server-side:**
```csharp
// In dispatch handler
await _notificationService.SendToGroupAsync(
    $"dispatch-{dispatchId}",
    "LocationUpdated",
    new { 
        dispatchId = dispatchId,
        latitude = location.Latitude,
        longitude = location.Longitude,
        timestamp = DateTime.UtcNow
    }
);
```

**Client-side:**
```javascript
// Join dispatch group
await notificationConnection.invoke("JoinGroup", `dispatch-${dispatchId}`);

// Listen for location updates
notificationConnection.on("LocationUpdated", (data) => {
  updateMapMarker(data.dispatchId, data.latitude, data.longitude);
});
```

### Example 3: Chat Integration

**Client-side:**
```javascript
const chatConnection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/chat", {
    accessTokenFactory: () => token
  })
  .build();

await chatConnection.start();

// Join conversation
await chatConnection.invoke("JoinConversation", conversationId);

// Send message
await chatConnection.invoke("SendMessage", conversationId, "Hello!");

// Listen for messages
chatConnection.on("ReceiveMessage", (message) => {
  displayMessage(message);
});

// Typing indicators
let typingTimeout;
chatConnection.on("UserTyping", (data) => {
  showTypingIndicator(data.userId, data.userName);
  clearTimeout(typingTimeout);
  typingTimeout = setTimeout(() => {
    hideTypingIndicator(data.userId);
  }, 3000);
});
```

---

## CORS Configuration

Ensure your CORS configuration allows SignalR connections:

```json
{
  "Cors": {
    "AllowedOrigins": [
      "http://localhost:3000",
      "https://your-frontend-domain.com"
    ]
  }
}
```

The CORS policy must:
- Allow credentials (`AllowCredentials()`)
- Allow the origin of your frontend
- Allow WebSocket connections

---

## Error Handling

### Connection Errors

```javascript
connection.onclose((error) => {
  if (error) {
    console.error("Connection closed with error:", error);
  } else {
    console.log("Connection closed");
  }
  
  // Attempt to reconnect
  setTimeout(() => {
    connection.start();
  }, 5000);
});
```

### Reconnection

SignalR supports automatic reconnection:

```javascript
const connection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/notifications", {
    accessTokenFactory: () => token
  })
  .withAutomaticReconnect([0, 2000, 10000, 30000]) // Reconnect delays in ms
  .build();
```

---

## Testing SignalR Connections

### Using Browser Console

```javascript
// Connect to notification hub
const connection = new signalR.HubConnectionBuilder()
  .withUrl("https://localhost:5001/hubs/notifications?access_token=YOUR_TOKEN")
  .build();

await connection.start();
console.log("Connected!");

// Listen for notifications
connection.on("NotificationReceived", (data) => {
  console.log("Notification:", data);
});
```

### Using Postman/HTTP Clients

SignalR uses WebSocket protocol, so you cannot test it directly with standard HTTP clients. Use:
- Browser console (as shown above)
- SignalR client libraries
- WebSocket testing tools

---

## Security Considerations

1. **JWT Authentication**: All hubs require valid JWT tokens
2. **CORS**: Configure allowed origins properly
3. **Group Access**: Users can only join groups they have permission for
4. **Rate Limiting**: Consider implementing rate limiting for SignalR connections

---

## Troubleshooting

### Connection Fails

1. **Check Token**: Ensure JWT token is valid and not expired
2. **Check CORS**: Verify CORS allows your origin
3. **Check URL**: Ensure using correct protocol (wss:// for HTTPS, ws:// for HTTP)
4. **Check Network**: Verify WebSocket connections are not blocked

### Not Receiving Messages

1. **Check Groups**: Ensure user is in the correct group
2. **Check Method Names**: Verify method names match between server and client
3. **Check Connection**: Verify connection is active (`connection.state`)

### Authentication Errors

1. **Token Format**: Ensure token is passed as `access_token` query parameter
2. **Token Validity**: Check token expiration
3. **Token Claims**: Verify user has required claims/roles

---

## Additional Resources

- [SignalR Documentation](https://docs.microsoft.com/aspnet/core/signalr/)
- [SignalR JavaScript Client](https://docs.microsoft.com/aspnet/core/signalr/javascript-client)
- [SignalR Authentication](https://docs.microsoft.com/aspnet/core/signalr/authn-and-authz)

