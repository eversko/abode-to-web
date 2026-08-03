import {
  Boxes,
  CircleDot,
  Gauge,
  Menu,
  Server,
  Settings,
  X,
} from 'lucide-react'
import { useEffect, useState } from 'react'
import { NavLink, Outlet, useLocation } from 'react-router'

const navigation = [
  { to: '/', label: 'Overview', icon: Gauge, end: true },
  { to: '/projects', label: 'Projects', icon: Boxes },
  { to: '/system', label: 'System', icon: Server },
  { to: '/settings', label: 'Settings', icon: Settings },
]

// Deployments/Activity left the sidebar (log pages reached from the overview
// log dropdown and project drawers) but keep their titles.
const pageTitle = (pathname: string) => {
  if (pathname.startsWith('/projects/new')) return 'Add project'
  if (/^\/projects\/[^/]+/.test(pathname)) return 'Project'
  if (pathname.startsWith('/deployments')) return 'Deployments'
  if (pathname.startsWith('/activity')) return 'Activity'
  return navigation.find((item) => item.to !== '/' && pathname.startsWith(item.to))?.label ?? 'Overview'
}

export function AppShell() {
  const [menuOpen, setMenuOpen] = useState(false)
  const [online, setOnline] = useState(navigator.onLine)
  const location = useLocation()

  useEffect(() => {
    setMenuOpen(false)
  }, [location.pathname])

  useEffect(() => {
    const markOnline = () => setOnline(true)
    const markOffline = () => setOnline(false)
    window.addEventListener('online', markOnline)
    window.addEventListener('offline', markOffline)
    return () => {
      window.removeEventListener('online', markOnline)
      window.removeEventListener('offline', markOffline)
    }
  }, [])

  return (
    <div className="app-shell">
      <a className="skip-link" href="#main-content">Skip to main content</a>
      <div className="mobile-header">
        <Brand />
        <button type="button" className="icon-button" aria-label={menuOpen ? 'Close navigation' : 'Open navigation'} aria-expanded={menuOpen} onClick={() => setMenuOpen((value) => !value)}>
          {menuOpen ? <X aria-hidden="true" /> : <Menu aria-hidden="true" />}
        </button>
      </div>
      {menuOpen && <button type="button" className="sidebar-scrim" aria-label="Close navigation" onClick={() => setMenuOpen(false)} />}
      <aside className={`sidebar ${menuOpen ? 'sidebar--open' : ''}`} aria-label="Primary">
        <Brand />
        <nav className="sidebar__nav">
          {navigation.map(({ to, label, icon: Icon, end }) => (
            <NavLink key={to} to={to} end={end} className={({ isActive }) => `nav-link ${isActive ? 'nav-link--active' : ''}`}>
              <Icon aria-hidden="true" size={18} />
              <span>{label}</span>
            </NavLink>
          ))}
        </nav>
        <div className="sidebar__footer">
          <div className={`connection-state ${online ? '' : 'connection-state--offline'}`}>
            <CircleDot aria-hidden="true" size={15} />
            <div><strong>{online ? 'Local connection' : 'Browser offline'}</strong><span>{online ? '127.0.0.1:4545' : 'Waiting to reconnect'}</span></div>
          </div>
          <span className="version-label">Abode to Web 0.1.0</span>
        </div>
      </aside>
      <div className="workspace">
        {!online && <div className="offline-banner" role="status">You are offline. Existing applications continue running under the Abode to Web service.</div>}
        <header className="topbar">
          <div>
            <span className="eyebrow">Personal hosting</span>
            <h1>{pageTitle(location.pathname)}</h1>
          </div>
          <div className="topbar__state"><span aria-hidden="true" />Service healthy</div>
        </header>
        <main id="main-content" className="main-content" tabIndex={-1}>
          <Outlet />
        </main>
      </div>
    </div>
  )
}

function Brand() {
  return (
    <NavLink to="/" className="brand" aria-label="Abode to Web overview">
      <span className="brand__mark" aria-hidden="true">
        <svg viewBox="0 0 64 64" width="20" height="20" fill="none" xmlns="http://www.w3.org/2000/svg">
          <path d="M9 49 21.5 15 34 49M14 37h15" stroke="#a8e6cf" strokeLinecap="round" strokeLinejoin="round" strokeWidth="5" />
          <path d="m34 19 5.5 30L47 33l7.5 16L60 19" stroke="#f4f7f5" strokeLinecap="round" strokeLinejoin="round" strokeWidth="4.5" />
          <path d="M28 29h9m-3-3 3 3-3 3" stroke="#a8e6cf" strokeLinecap="round" strokeLinejoin="round" strokeWidth="2.5" />
        </svg>
      </span>
      <span className="brand__word">ABODE TO WEB</span>
    </NavLink>
  )
}
