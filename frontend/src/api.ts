export type RoomTypeResult = {
  hotelId: number
  hotelName: string
  roomTypeId: number
  roomTypeName: string
  pricePerNightCents: number
  totalPriceCents: number
  availableRooms: number
}

export type BookingCreated = {
  bookingId: string
  totalPriceCents: number
  expiresAt: string
}

export type BookingResult = {
  bookingId: string
  hotelName: string
  roomTypeName: string
  checkIn: string
  checkOut: string
  status: 'Held' | 'Confirmed' | 'Expired' | 'Cancelled'
  totalPriceCents: number
  expiresAt: string
  refundAmountCents: number | null
}

export function searchRooms(city: string, checkIn: string, checkOut: string) {
  const params = new URLSearchParams({ city, checkIn, checkOut })
  return request<RoomTypeResult[]>(`/api/search?${params}`)
}

export function createBooking(roomTypeId: number, checkIn: string, checkOut: string, token: string) {
  return request<BookingCreated>('/api/bookings', {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      Authorization: `Bearer ${token}`,
    },
    body: JSON.stringify({ roomTypeId, checkIn, checkOut }),
  })
}

export function listBookings(token: string) {
  return request<BookingResult[]>('/api/bookings', {
    headers: { Authorization: `Bearer ${token}` },
  })
}

export function cancelBooking(bookingId: string, token: string) {
  return request<{ refundAmountCents: number }>(`/api/bookings/${bookingId}/cancel`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}` },
  })
}

export function startCheckout(bookingId: string, token: string) {
  return request<{ checkoutUrl: string }>(`/api/bookings/${bookingId}/checkout`, {
    method: 'POST',
    headers: { Authorization: `Bearer ${token}` },
  })
}

// Errors from the Gateway are ProblemDetails JSON. Rate limit (429) responses have no body.
async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init)
  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    throw new Error(problem?.detail ?? problem?.title ?? `Request failed (${response.status})`)
  }
  return response.json()
}
