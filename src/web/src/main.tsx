import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import { BrowserRouter, Route, Routes } from 'react-router';
import { JobsProvider } from './jobs';
import { JobList } from './JobList';
import { JobDetailPage } from './JobDetail';
import { SettingsPage } from './Settings';
import './styles.css';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <BrowserRouter>
      <JobsProvider>
        <Routes>
          <Route path="/" element={<JobList />} />
          <Route path="/jobs/:id" element={<JobDetailPage />} />
          <Route path="/settings" element={<SettingsPage />} />
        </Routes>
      </JobsProvider>
    </BrowserRouter>
  </StrictMode>,
);
