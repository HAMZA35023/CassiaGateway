import { ComponentFixture, TestBed } from '@angular/core/testing';

import { UpgradePrepDialog } from './upgrade-prep-dialog';

describe('UpgradePrepDialog', () => {
  let component: UpgradePrepDialog;
  let fixture: ComponentFixture<UpgradePrepDialog>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [UpgradePrepDialog]
    })
    .compileComponents();

    fixture = TestBed.createComponent(UpgradePrepDialog);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
