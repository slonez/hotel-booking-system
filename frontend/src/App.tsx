import { useCallback, useEffect, useState, type FormEvent } from 'react'
import { Show, SignInButton, UserButton, useAuth } from '@clerk/react'
import {
  createBooking,
  listBookings,
  searchRooms,
  type BookingCreated,
  type BookingResult,
  type RoomTypeResult,
} from './api'
import { euros } from './format'
import MyBookings from './MyBookings'

type Stay = { city: string; checkIn: string; checkOut: string }

export default function App() {
  const { getToken, isSignedIn } = useAuth()
  const [form, setForm] = useState<Stay>({ city: 'Helsinki', checkIn: '', checkOut: '' })
  // Booking uses the dates of the last search, not whatever is in the form now.
  const [searched, setSearched] = useState<Stay | null>(null)
  const [results, setResults] = useState<RoomTypeResult[]>([])
  const [booking, setBooking] = useState<BookingCreated | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [myBookings, setMyBookings] = useState<BookingResult[]>([])

  const loadMyBookings = useCallback(async () => {
    try {
      const token = await getToken()
      setMyBookings(await listBookings(token!))
    } catch (err) {
      setError((err as Error).message)
    }
  }, [getToken])

  // Load the user's bookings when they sign in.
  useEffect(() => {
    // State is set after the request completes, not synchronously in the effect.
    // oxlint-disable-next-line react/set-state-in-effect
    if (isSignedIn) void loadMyBookings()
  }, [isSignedIn, loadMyBookings])

  async function run(action: () => Promise<void>) {
    setError(null)
    try {
      await action()
    } catch (err) {
      setError((err as Error).message)
    }
  }

  const search = (e: FormEvent) => {
    e.preventDefault()
    return run(async () => {
      setBooking(null)
      setResults(await searchRooms(form.city, form.checkIn, form.checkOut))
      setSearched(form)
    })
  }

  const book = (roomTypeId: number) =>
    run(async () => {
      const stay = searched!
      const token = await getToken()
      setBooking(await createBooking(roomTypeId, stay.checkIn, stay.checkOut, token!))
      setResults(await searchRooms(stay.city, stay.checkIn, stay.checkOut))
      await loadMyBookings()
    })

  return (
    <main>
      <header>
        <h1>Hotel Booking</h1>
        <Show when="signed-in">
          <UserButton />
        </Show>
        <Show when="signed-out">
          <SignInButton mode="modal" />
        </Show>
      </header>

      <form onSubmit={search}>
        <label>
          City
          <input value={form.city} onChange={(e) => setForm({ ...form, city: e.target.value })} required />
        </label>
        <label>
          Check-in
          <input type="date" value={form.checkIn} onChange={(e) => setForm({ ...form, checkIn: e.target.value })} required />
        </label>
        <label>
          Check-out
          <input type="date" value={form.checkOut} onChange={(e) => setForm({ ...form, checkOut: e.target.value })} required />
        </label>
        <button type="submit">Search</button>
      </form>

      {error && <p className="error">{error}</p>}

      {booking && (
        <p className="success">
          Room held until {new Date(booking.expiresAt).toLocaleTimeString('fi-FI')}. Total {euros(booking.totalPriceCents)}.
          Booking ID: {booking.bookingId}
        </p>
      )}

      {searched && (
        <table>
          <thead>
            <tr>
              <th>Hotel</th>
              <th>Room type</th>
              <th>Per night</th>
              <th>Total</th>
              <th>Available</th>
              <th />
            </tr>
          </thead>
          <tbody>
            {results.map((rt) => (
              <tr key={rt.roomTypeId}>
                <td>{rt.hotelName}</td>
                <td>{rt.roomTypeName}</td>
                <td>{euros(rt.pricePerNightCents)}</td>
                <td>{euros(rt.totalPriceCents)}</td>
                <td>{rt.availableRooms}</td>
                <td>
                  {rt.availableRooms === 0 ? (
                    'Sold out'
                  ) : (
                    <>
                      <Show when="signed-in">
                        <button onClick={() => book(rt.roomTypeId)}>Book</button>
                      </Show>
                      <Show when="signed-out">
                        <SignInButton mode="modal">
                          <button>Sign in to book</button>
                        </SignInButton>
                      </Show>
                    </>
                  )}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}
      {searched && results.length === 0 && <p>No hotels found in {searched.city}.</p>}

      <Show when="signed-in">
        <h2>My bookings</h2>
        <MyBookings bookings={myBookings} />
      </Show>
    </main>
  )
}
