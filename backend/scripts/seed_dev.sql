-- =============================================================================
-- Anamnys AI — Dev Seed Data
-- Run against: anamnys_dev
--
-- Usage:
--   psql -U postgres -d anamnys_dev -f seed_dev.sql
--
-- Uses pgcrypto for bcrypt password hashing (same algorithm as BCrypt.Net).
-- =============================================================================

CREATE EXTENSION IF NOT EXISTS pgcrypto;

-- =============================================================================
-- PROVIDERS
-- =============================================================================

INSERT INTO "Providers" (
    "Id", "Email", "Name", "PasswordHash",
    "Specialty", "PreferredNoteFormat", "BillingSystem", "CreatedAt"
)
VALUES
(
    'a1000000-0000-0000-0000-000000000001',
    'sarah.mh@clinicaldraft.local',
    'Dr. Sarah Mendez',
    crypt('Dev1234!', gen_salt('bf', 11)),
    'MentalHealth',
    'DAP',
    0,               -- UsCpt (0): CPT + ICD-10-CM
    NOW()
),
(
    'a1000000-0000-0000-0000-000000000002',
    'james.pt@clinicaldraft.local',
    'Dr. James Okafor',
    crypt('Dev1234!', gen_salt('bf', 11)),
    'PhysicalTherapy',
    'SOAP',
    0,               -- UsCpt (0): CPT + ICD-10-CM
    NOW()
),
-- Extra dev providers for testing international billing engines
(
    'a1000000-0000-0000-0000-000000000003',
    'ana.ca@clinicaldraft.local',
    'Dr. Ana Silva (Canada)',
    crypt('Dev1234!', gen_salt('bf', 11)),
    'MentalHealth',
    'DAP',
    1,               -- CanadaOhip (1): OHIP fee codes + ICD-10-CA
    NOW()
),
(
    'a1000000-0000-0000-0000-000000000004',
    'carlos.br@clinicaldraft.local',
    'Dr. Carlos Souza (Brazil)',
    crypt('Dev1234!', gen_salt('bf', 11)),
    'MentalHealth',
    'DAP',
    2,               -- BrazilTuss (2): TUSS codes + CID-10
    NOW()
),
(
    'a1000000-0000-0000-0000-000000000005',
    'marie.eu@clinicaldraft.local',
    'Dr. Marie Dupont (Europe)',
    crypt('Dev1234!', gen_salt('bf', 11)),
    'PhysicalTherapy',
    'SOAP',
    3,               -- EuropeGeneric (3): SNOMED CT procedures + ICD-10 national modification
    NOW()
)
ON CONFLICT ("Email") DO NOTHING;


-- =============================================================================
-- PATIENTS  (5 for Mental Health provider, 5 for PT provider)
-- =============================================================================

INSERT INTO "Patients" (
    "Id", "ProviderId", "FirstName", "LastName",
    "DateOfBirth", "Diagnoses", "CurrentMedications",
    "TreatmentPlan", "LastVisit", "CreatedAt"
)
VALUES

-- Mental Health patients
(
    'b1000000-0000-0000-0000-000000000001',
    'a1000000-0000-0000-0000-000000000001',
    'Alice', 'Torres',
    '1988-03-14',
    '["Major Depressive Disorder, recurrent moderate (F33.1)", "Generalized Anxiety Disorder (F41.1)"]',
    '["Sertraline 100mg QD", "Buspirone 10mg BID"]',
    'Weekly individual psychotherapy (CBT). Goals: reduce PHQ-9 to <10, improve sleep hygiene.',
    NOW() - INTERVAL '3 days',
    NOW() - INTERVAL '6 months'
),
(
    'b1000000-0000-0000-0000-000000000002',
    'a1000000-0000-0000-0000-000000000001',
    'Marcus', 'Chen',
    '1995-07-22',
    '["PTSD (F43.10)", "Insomnia disorder (G47.00)"]',
    '["Prazosin 1mg QHS", "Hydroxyzine 25mg PRN"]',
    'Bi-weekly EMDR sessions. Goals: reduce PCL-5 score, establish consistent sleep schedule.',
    NOW() - INTERVAL '7 days',
    NOW() - INTERVAL '4 months'
),
(
    'b1000000-0000-0000-0000-000000000003',
    'a1000000-0000-0000-0000-000000000001',
    'Priya', 'Nair',
    '1979-11-05',
    '["Bipolar II Disorder (F31.81)"]',
    '["Lamotrigine 200mg QD", "Quetiapine 50mg QHS"]',
    'Monthly medication management + bi-weekly supportive therapy. Monitor mood log.',
    NOW() - INTERVAL '14 days',
    NOW() - INTERVAL '8 months'
),
(
    'b1000000-0000-0000-0000-000000000004',
    'a1000000-0000-0000-0000-000000000001',
    'Derek', 'Williams',
    '2001-01-30',
    '["Social Anxiety Disorder (F40.10)", "ADHD, predominantly inattentive (F90.0)"]',
    '["Escitalopram 20mg QD", "Amphetamine salts 20mg QAM"]',
    'Weekly CBT targeting social avoidance. Coordinate with prescriber on ADHD management.',
    NOW() - INTERVAL '5 days',
    NOW() - INTERVAL '3 months'
),
(
    'b1000000-0000-0000-0000-000000000005',
    'a1000000-0000-0000-0000-000000000001',
    'Linda', 'Foster',
    '1965-09-18',
    '["Persistent Depressive Disorder (F34.1)", "Panic Disorder (F41.0)"]',
    '["Venlafaxine XR 150mg QD", "Clonazepam 0.5mg PRN (tapering)"]',
    'Weekly ACT therapy. Goal: discontinue benzodiazepine, build distress tolerance skills.',
    NOW() - INTERVAL '2 days',
    NOW() - INTERVAL '12 months'
),

-- Physical Therapy patients
(
    'b1000000-0000-0000-0000-000000000006',
    'a1000000-0000-0000-0000-000000000002',
    'Robert', 'Kim',
    '1972-06-10',
    '["Lumbar disc herniation L4-L5 (M51.16)", "Chronic low back pain (M54.5)"]',
    '["Ibuprofen 600mg TID PRN", "Cyclobenzaprine 5mg QHS PRN"]',
    '3x/week PT for 8 weeks. Focus: core stabilization, McKenzie extension exercises, ergonomic education.',
    NOW() - INTERVAL '1 day',
    NOW() - INTERVAL '3 weeks'
),
(
    'b1000000-0000-0000-0000-000000000007',
    'a1000000-0000-0000-0000-000000000002',
    'Carla', 'Russo',
    '1990-04-27',
    '["Rotator cuff tear, right shoulder, partial thickness (M75.101)", "Shoulder impingement (M75.1)"]',
    '["Naproxen 500mg BID PRN"]',
    'Post-surgical rotator cuff repair rehab protocol. Phase 2: active ROM and progressive strengthening.',
    NOW() - INTERVAL '3 days',
    NOW() - INTERVAL '6 weeks'
),
(
    'b1000000-0000-0000-0000-000000000008',
    'a1000000-0000-0000-0000-000000000002',
    'Tom', 'Andersen',
    '1958-12-03',
    '["Knee osteoarthritis, bilateral (M17.0)", "Patellofemoral pain syndrome (M22.2)"]',
    '["Acetaminophen 1000mg QID PRN", "Meloxicam 15mg QD"]',
    'Conservative management. Quadriceps strengthening, aquatic therapy, patellar taping.',
    NOW() - INTERVAL '5 days',
    NOW() - INTERVAL '2 months'
),
(
    'b1000000-0000-0000-0000-000000000009',
    'a1000000-0000-0000-0000-000000000002',
    'Yemi', 'Adebayo',
    '2000-08-15',
    '["Anterior cruciate ligament tear, left knee (S83.511A)", "Post-op ACL reconstruction"]',
    '["Ibuprofen 800mg TID x2 weeks"]',
    'ACL reconstruction rehab. Currently week 6: full weight bearing, quad sets, mini-squats.',
    NOW() - INTERVAL '4 days',
    NOW() - INTERVAL '6 weeks'
),
(
    'b1000000-0000-0000-0000-000000000010',
    'a1000000-0000-0000-0000-000000000002',
    'Helen', 'Marsh',
    '1948-02-22',
    '["Hip fracture, left femoral neck, post-surgical (M84.352)", "Osteoporosis (M81.0)"]',
    '["Alendronate 70mg weekly", "Calcium/Vit D supplement"]',
    'Post-hip replacement rehab. Weight bearing as tolerated. Focus: gait training, fall prevention.',
    NOW() - INTERVAL '6 days',
    NOW() - INTERVAL '4 weeks'
)
ON CONFLICT ("Id") DO NOTHING;


-- =============================================================================
-- SAMPLE NOTES
-- Timestamps are built with to_char() so INTERVAL stays at SQL level,
-- not nested inside a string literal (which caused the syntax error).
-- =============================================================================

INSERT INTO "Notes" (
    "Id", "PatientId", "ProviderId",
    "Status", "InputMode",
    "RawTranscript", "StructuredContent",
    "BillingCodes", "AuditTrail",
    "CreatedAt", "SignedAt"
)
VALUES

-- Alice Torres — signed DAP note (Mental Health)
(
    'c1000000-0000-0000-0000-000000000001',
    'b1000000-0000-0000-0000-000000000001',
    'a1000000-0000-0000-0000-000000000001',
    'Signed',
    'VoiceBatch',
    'Patient reports sleep has improved slightly, averaging 6 hours vs 4 previously. Mood remains low but denies SI. Engaged in thought challenging exercise re: work performance concerns. Will continue homework tracking.',
    '{"Format":0,"Sections":{"Data":"Patient reports sleep improvement to ~6 hrs/night. PHQ-9 today: 14 (moderate). Denies suicidal ideation. Engaged and cooperative throughout session.","Assessment":"MDD recurrent moderate with partial response to current pharmacotherapy. GAD symptoms secondary. Sleep improving as proxy indicator. Risk: low.","Plan":"Continue weekly CBT. Assign automatic thought record homework. Follow up with prescriber re: Sertraline dose. Return in 1 week."},"GeneratedAt":"2026-08-01T10:00:00Z"}',
    '[{"CptCode":"90837","Icd10Code":"F33.1","Description":"Psychotherapy, 60 minutes","ConfidenceScore":0.92,"DenialRiskScore":15,"Modifiers":[],"MissingDocumentation":[]}]',
    '[{"Timestamp":"2026-08-01T09:30:00Z","Action":"ai_draft","ActorId":"00000000-0000-0000-0000-000000000000"},{"Timestamp":"2026-08-01T10:00:00Z","Action":"signed","ActorId":"a1000000-0000-0000-0000-000000000001"}]',
    NOW() - INTERVAL '3 days',
    NOW() - INTERVAL '3 days' + INTERVAL '1 hour'
),

-- Robert Kim — signed SOAP note (Physical Therapy)
(
    'c1000000-0000-0000-0000-000000000002',
    'b1000000-0000-0000-0000-000000000006',
    'a1000000-0000-0000-0000-000000000002',
    'Signed',
    'Text',
    'Patient reports pain 4/10 at rest, 7/10 with prolonged sitting. Completed HEP 3x this week. Tight hamstrings and hip flexors. Lumbar extension ROM 15 degrees limited. Performed McKenzie extensions x10, hip flexor stretching, core stabilization with dead bugs.',
    '{"Format":1,"Sections":{"Subjective":"Patient reports pain 4/10 at rest, 7/10 with prolonged sitting over 30 min. Reports completing HEP 3x this week. No radiation of symptoms.","Objective":"Lumbar extension ROM: 15 degrees (limited). SLR negative bilaterally. Hamstring tightness: 70 degrees passive SLR. Core activation fair. Palpation: paraspinal tenderness L4-L5.","Assessment":"Lumbar disc herniation L4-L5 with moderate functional limitation. Patient progressing with HEP compliance. Pain improving from initial 8/10.","Plan":"Continue McKenzie protocol, progress to lumbar stabilization phase. Add prone press-ups. Ergonomic consult next visit. RTC 3x/week x 4 more weeks."},"GeneratedAt":"2026-08-01T14:00:00Z"}',
    '[{"CptCode":"97110","Icd10Code":"M54.5","Description":"Therapeutic exercises","ConfidenceScore":0.88,"DenialRiskScore":20,"Modifiers":["GP"],"MissingDocumentation":[]},{"CptCode":"97140","Icd10Code":"M51.16","Description":"Manual therapy techniques","ConfidenceScore":0.85,"DenialRiskScore":25,"Modifiers":["GP"],"MissingDocumentation":[]}]',
    '[{"Timestamp":"2026-08-01T13:40:00Z","Action":"ai_draft","ActorId":"00000000-0000-0000-0000-000000000000"},{"Timestamp":"2026-08-01T14:00:00Z","Action":"signed","ActorId":"a1000000-0000-0000-0000-000000000002"}]',
    NOW() - INTERVAL '1 day',
    NOW() - INTERVAL '1 day' + INTERVAL '30 minutes'
)
ON CONFLICT ("Id") DO NOTHING;


-- =============================================================================
-- VERIFY
-- =============================================================================

SELECT 'Providers' AS "Table", COUNT(*) AS "Rows" FROM "Providers"
UNION ALL
SELECT 'Patients', COUNT(*) FROM "Patients"
UNION ALL
SELECT 'Notes', COUNT(*) FROM "Notes";
