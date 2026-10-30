export const euros = (cents: number) =>
  (cents / 100).toLocaleString('fi-FI', { style: 'currency', currency: 'EUR' })
