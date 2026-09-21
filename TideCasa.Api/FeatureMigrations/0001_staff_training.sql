CREATE TABLE tide_staff_course_assignments (
    tenant_id TEXT NOT NULL REFERENCES bartide_customers(id),
    member_id TEXT NOT NULL REFERENCES bartide_enhanced_members(id),
    course_id TEXT NOT NULL REFERENCES fit_courses(id),
    active INTEGER NOT NULL DEFAULT 1 CHECK(active IN (0,1)),
    version INTEGER NOT NULL DEFAULT 0,
    assigned_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    PRIMARY KEY(member_id,course_id)
);
CREATE INDEX tide_staff_assignments_tenant ON tide_staff_course_assignments(tenant_id,course_id,active);
CREATE TABLE tide_staff_lesson_progress (
    tenant_id TEXT NOT NULL REFERENCES bartide_customers(id),
    member_id TEXT NOT NULL REFERENCES bartide_enhanced_members(id),
    lesson_id TEXT NOT NULL REFERENCES fit_lessons(id) ON DELETE CASCADE,
    completed INTEGER NOT NULL DEFAULT 0 CHECK(completed IN (0,1)),
    updated_at TEXT NOT NULL,
    PRIMARY KEY(member_id,lesson_id)
);
