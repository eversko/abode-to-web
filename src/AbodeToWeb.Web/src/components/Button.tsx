import { LoaderCircle, type LucideIcon } from 'lucide-react'
import { forwardRef, type ButtonHTMLAttributes, type ReactNode } from 'react'

type ButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  variant?: 'primary' | 'secondary' | 'quiet' | 'danger'
  icon?: LucideIcon
  busy?: boolean
  children: ReactNode
}

export const Button = forwardRef<HTMLButtonElement, ButtonProps>(function Button(
  { variant = 'secondary', icon: Icon, busy = false, className = '', children, disabled, ...props },
  ref,
) {
  return (
    <button
      ref={ref}
      className={`button button--${variant} ${className}`}
      disabled={busy ? true : disabled}
      aria-busy={busy || undefined}
      {...props}
    >
      {busy ? <LoaderCircle aria-hidden="true" className="button__spinner" size={16} /> : Icon ? <Icon aria-hidden="true" size={16} /> : null}
      <span>{children}</span>
    </button>
  )
})
