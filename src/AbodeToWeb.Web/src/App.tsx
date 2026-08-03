import { Navigate, Route, Routes } from 'react-router'
import { AppShell } from './layout/AppShell'
import { ActivityPage } from './pages/ActivityPage'
import { DeploymentsPage } from './pages/DeploymentsPage'
import { OnboardingPage } from './pages/OnboardingPage'
import { OverviewPage } from './pages/OverviewPage'
import { ProjectDetailPage } from './pages/ProjectDetailPage'
import { ProjectsPage } from './pages/ProjectsPage'
import { SettingsPage } from './pages/SettingsPage'
import { SystemPage } from './pages/SystemPage'
import { EmptyState } from './components/ResourceState'
import { Compass } from 'lucide-react'
import { Link } from 'react-router'

export function AppRoutes() {
  return (
    <Routes>
      <Route element={<AppShell />}>
        <Route index element={<OverviewPage />} />
        <Route path="projects" element={<ProjectsPage />} />
        <Route path="projects/new" element={<OnboardingPage />} />
        <Route path="projects/:projectId" element={<ProjectDetailPage />} />
        <Route path="projects/:projectId/:tab" element={<ProjectDetailPage />} />
        <Route path="external-sites" element={<Navigate to="/projects" replace />} />
        <Route path="deployments" element={<DeploymentsPage />} />
        <Route path="activity" element={<ActivityPage />} />
        <Route path="system" element={<SystemPage />} />
        <Route path="settings" element={<SettingsPage />} />
        <Route path="*" element={<NotFound />} />
      </Route>
    </Routes>
  )
}

function NotFound() {
  return <EmptyState icon={<Compass />} title="Page not found" action={<Link className="button button--primary" to="/">Return to overview</Link>}>The address does not match an Abode to Web page.</EmptyState>
}
