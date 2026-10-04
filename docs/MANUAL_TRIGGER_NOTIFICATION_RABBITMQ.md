# Manually Trigger a Push / Notification via RabbitMQ

Publish a MassTransit message straight onto the bus from the RabbitMQ management
console. The API's `SendPushConsumer` picks it up and delivers, exactly as if
another backend module had published it.

## Names to use

Derived from the code — no custom endpoint/entity name formatter is configured
(`Program.cs:702-777`), so MassTransit defaults apply.

| Purpose | Name |
|---|---|
| Exchange — push | `BeeLogistics.Shared.Contracts:SendPushRequested` |
| Exchange — unified | `BeeLogistics.Shared.Contracts:SendNotificationRequested` |
| Consumer queue — push | `SendPush` |
| Consumer queue — unified | `SendNotification` |
| Content-type (message property) | `application/vnd.masstransit+json` |

## Steps (push notification)

1. **Open the management UI.** Go to <http://localhost:15672> and log in — dev is
   `guest` / `guest`, otherwise your `RabbitMq` username / password from Vault.

2. **Confirm the exchange exists.** Open the **Exchanges** tab and find
   `BeeLogistics.Shared.Contracts:SendPushRequested`. If it's missing, the API
   isn't running/connected — start it first, or the message routes nowhere.

3. **Open the publish form.** Click that exchange, then expand the
   **Publish message** panel.

4. **Set routing key + content-type.**
   - **Routing key:** leave empty.
   - **Properties:** add one — key `content_type`, value
     `application/vnd.masstransit+json`. **Required** — without it the message is
     rejected to the error queue.

5. **Paste the payload.** The real fields live inside `message`:

   ```json
   {
     "messageId": "11111111-1111-1111-1111-111111111111",
     "messageType": [
       "urn:message:BeeLogistics.Shared.Contracts:SendPushRequested"
     ],
     "message": {
       "title": "Manual test push 🚚",
       "body": "Triggered by hand from RabbitMQ management UI.",
       "appType": "driver",
       "userId": "3f9a2c14-8b7e-4d21-9c6a-1e2f3a4b5c6d",
       "data": { "type": "manual_test" },
       "burst": false,
       "recordId": "00000000-0000-0000-0000-000000000000"
     }
   }
   ```

   Swap `userId` for a real user GUID with a registered device token, and set
   `appType` to `customer` or `driver`. To hit one device instead, drop
   `userId`/`appType` and use `"deviceToken": "ExponentPushToken[...]"`.

6. **Publish & verify.** Click **Publish message** — you should see
   "Message published." The backend logs the trace line:

   ```
   [NOTIF-TRACE] 9. SendPushConsumer RECEIVED from SendPush queue. title='Manual test push', recordId=00000000-...
   ```

   In the **Queues** tab the `SendPush` count ticks up, then back to 0.

> **History rows:** the empty `recordId` means the consumer delivers but writes no
> history row — expected for a manual test. Use `POST /api/notifications/send` if
> you want it logged on the backoffice "Sent Notifications" page.

## Variant — unified email / SMS / push

Same steps, but publish to exchange
`BeeLogistics.Shared.Contracts:SendNotificationRequested` with this payload:

```json
{
  "messageId": "33333333-3333-3333-3333-333333333333",
  "messageType": [
    "urn:message:BeeLogistics.Shared.Contracts:SendNotificationRequested"
  ],
  "message": {
    "channels": ["push"],
    "push": {
      "title": "Manual unified test",
      "body": "Sent via SendNotificationRequested.",
      "appType": "customer",
      "userId": "3f9a2c14-8b7e-4d21-9c6a-1e2f3a4b5c6d"
    }
  }
}
```

## If it doesn't work

- **Message lands in `SendPush_error` / disappears:** almost always a missing or
  misspelled `content_type` property, or a typo in the type URN — it must be
  exactly `urn:message:BeeLogistics.Shared.Contracts:SendPushRequested`.
- **Nothing consumes it:** the API isn't running, so the exchange→queue binding is
  inactive and RabbitMQ silently discards the publish. Confirm the `SendPush`
  queue exists and shows a consumer in the **Queues** tab.

---

_References: `src/BeeLogistics.Api/Program.cs:702-777`,
`src/Modules/BeeLogistics.Modules.Notification/Application/Consumers/SendPushConsumer.cs`,
`src/BeeLogistics.Shared/Contracts/NotificationContracts.cs`._
