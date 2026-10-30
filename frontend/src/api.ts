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

// Errors from the Gateway are ProblemDetails JSON. Rate limit (429) responses have no body.
async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, init)
  if (!response.ok) {
    const problem = await response.json().catch(() => null)
    throw new Error(problem?.detail ?? problem?.title ?? `Request failed (${response.status})`)
  }
  return response.json()
}
