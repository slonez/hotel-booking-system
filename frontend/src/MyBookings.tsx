import type { BookingResult } from './api'
import { euros } from './format'

type Props = {
  bookings: BookingResult[]
  onCancel: (bookingId: string) => void
}

export default function MyBookings({ bookings, onCancel }: Props) {
  if (bookings.length === 0) return <p>No bookings yet.</p>

  return (
    <table>
      <thead>
        <tr>
          <th>Hotel</th>
          <th>Room type</th>
          <th>Dates</th>
          <th>Total</th>
          <th>Status</th>
          <th />
        </tr>
      </thead>
      <tbody>
        {bookings.map((b) => (
          <tr key={b.bookingId}>
            <td>{b.hotelName}</td>
            <td>{b.roomTypeName}</td>
            <td>
              {b.checkIn} – {b.checkOut}
            </td>
            <td>{euros(b.totalPriceCents)}</td>
            <td>
              {b.status}
              {b.status === 'Held' && ` until ${new Date(b.expiresAt).toLocaleTimeString('fi-FI')}`}
              {b.refundAmountCents !== null && `, refund ${euros(b.refundAmountCents)}`}
            </td>
            <td>{b.status === 'Confirmed' && <button onClick={() => onCancel(b.bookingId)}>Cancel</button>}</td>
          </tr>
        ))}
      </tbody>
    </table>
  )
}
